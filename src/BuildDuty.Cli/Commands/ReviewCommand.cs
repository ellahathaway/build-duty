using System.Text;
using System.Threading.Channels;
using BuildDuty.AI;
using BuildDuty.Core;
using GitHub.Copilot.SDK;
using Spectre.Console;

namespace BuildDuty.Cli.Commands;

internal sealed class ReviewCommand : BaseCommand<BaseSettings>
{
    private readonly IStorageProvider _storageProvider;
    private readonly CopilotAdapter _copilotAdapter;

    public ReviewCommand(
        IBuildDutyConfigProvider configProvider,
        IStorageProvider storageProvider,
        CopilotAdapter copilotAdapter)
        : base(configProvider)
    {
        _storageProvider = storageProvider;
        _copilotAdapter = copilotAdapter;
    }

    protected override async Task<int> ExecuteCommandAsync(Spectre.Console.Cli.CommandContext context, BaseSettings settings)
    {
        while (true)
        {
            var unresolvedItems = (await _storageProvider.GetWorkItemsAsync())
                .OrderByDescending(item => item.UpdatedAt)
                .Where(item => !item.Resolved)
                .ToList();

            if (unresolvedItems.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No work items to review.[/]");
                return 0;
            }

            var itemChoices = unresolvedItems
                .Select(item =>
                {
                    var status = item.Resolved ? "[green]resolved[/]" : "[yellow]open[/]";
                    return $"{Markup.Escape(item.Id)} ({status}) {Markup.Escape(item.IssueSignature ?? "(unavailable: see details with `workitem show`)")}";
                })
                .ToList();

            var selectedLabels = AnsiConsole.Prompt(
                new MultiSelectionPrompt<string>()
                    .Title($"[bold]Review[/] -- {unresolvedItems.Count} unresolved work item(s). Select work items (space = toggle, enter = confirm):")
                    .NotRequired()
                    .HighlightStyle(new Style(Color.Teal))
                    .PageSize(20)
                    .AddChoices(itemChoices));

            if (selectedLabels.Count == 0)
            {
                return 0;
            }

            var selectedSet = selectedLabels.ToHashSet();
            var selectedItems = unresolvedItems
                .Where((_, idx) => selectedSet.Contains(itemChoices[idx]))
                .ToList();

            await ShowActionMenu(selectedItems);
        }
    }

    private async Task ShowActionMenu(List<WorkItem> selectedItems)
    {
        AnsiConsole.Clear();

        AnsiConsole.MarkupLine($"[bold]Selected {selectedItems.Count} work item(s):[/]");
        foreach (var item in selectedItems)
        {
            AnsiConsole.MarkupLine($"  [bold]*[/] {Markup.Escape(item.Id)}: {Markup.Escape(item.IssueSignature ?? "(no signature, see details with `workitem show`)")}");
        }
        AnsiConsole.WriteLine();

        var backLabel = "<- Back";
        var resolveLabel = "Mark as resolved";
        var agentLabel = "Ask agent";
        var detailLabel = "View details"; // TODO

        var actionChoices = new List<string>();
        if (selectedItems.Any(i => !i.Resolved))
        {
            actionChoices.Add(resolveLabel);
        }
        actionChoices.Add(agentLabel);
        if (selectedItems.Count == 1)
        {
            actionChoices.Add(detailLabel);
        }
        actionChoices.Add(backLabel);

        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Action:[/]")
                .HighlightStyle(new Style(Color.Teal))
                .AddChoices(actionChoices));

        if (action == backLabel)
        {
            return;
        }

        if (action == resolveLabel)
        {
            var reason = AnsiConsole.Ask<string>("[bold]Resolution reason:[/]");
            if (!string.IsNullOrWhiteSpace(reason))
            {
                foreach (var item in selectedItems.Where(i => !i.Resolved))
                {
                    item.Resolved = true;
                    item.ResolvedAt = DateTime.UtcNow;
                    item.UpdatedAt = DateTime.UtcNow;
                    await _storageProvider.SaveWorkItemAsync(item);
                }
                AnsiConsole.MarkupLine($"[green]{selectedItems.Count(i => !i.Resolved)} work item(s) resolved.[/]");
            }
            return;
        }

        if (action == agentLabel)
        {
            string instruction = AnsiConsole.Ask<string>("[bold]What should the agent do?[/]");
            if (string.IsNullOrWhiteSpace(instruction))
            {
                AnsiConsole.MarkupLine("[red]Instruction cannot be empty.[/]");
                return;
            }

            await RunInteractiveChat(selectedItems.Select(wi => wi.Id).ToList(), instruction);
            return;
        }
    }

    private async Task RunInteractiveChat(List<string> selectedWorkItemIds, string initialInstruction)
    {
        using var terminal = new ChatTerminal();
        var session = await _copilotAdapter.CreateSessionAsync(streaming: true, agent: CopilotAdapter.Agents.Review);
        RegisterChatStreamHandler(session, terminal);

        var inputChannel = Channel.CreateUnbounded<string?>();
        var agentCts = new CancellationTokenSource();

        // Background input reader
        var inputTask = Task.Run(() =>
        {
            while (true)
            {
                var input = terminal.ReadInput();
                inputChannel.Writer.TryWrite(input);
                if (input is null)
                {
                    break;
                }
            }
            inputChannel.Writer.TryComplete();
        });

        // Send initial prompt
        var initialPrompt = $"For the following work items(s) {string.Join(", ", selectedWorkItemIds)}, respond the the instruction, '{initialInstruction}'";
        terminal.WriteOutput($"\x1b[1m> {initialInstruction}\x1b[0m\n\n");
        var agentTask = Task.Run(() => _copilotAdapter.RunPromptAsync(session, initialPrompt, agentCts.Token));

        try
        {
            await foreach (var rawInput in inputChannel.Reader.ReadAllAsync())
            {
                if (rawInput is null)
                {
                    break;
                }

                var input = rawInput.Trim();
                if (input.Length == 0)
                {
                    continue;
                }

                if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase) ||
                    input.Equals("/quit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (input.Equals("/reset", StringComparison.OrdinalIgnoreCase))
                {
                    await CancelAgentAsync();
                    await session.DisposeAsync();
                    session = await _copilotAdapter.CreateSessionAsync(streaming: true, agent: CopilotAdapter.Agents.Review);
                    RegisterChatStreamHandler(session, terminal);
                    terminal.WriteOutput("\n\x1b[2m(session reset)\x1b[0m\n");
                    continue;
                }

                // Cancel any in-flight response so the new message can be sent immediately
                if (agentTask is { IsCompleted: false })
                {
                    await CancelAgentAsync();
                    terminal.WriteOutput("\n\x1b[2m(interrupted)\x1b[0m\n");
                }

                terminal.WriteOutput($"\n\x1b[1m> {input}\x1b[0m\n\n");
                agentCts = new CancellationTokenSource();
                agentTask = Task.Run(() => _copilotAdapter.RunPromptAsync(session, input, agentCts.Token));
            }
        }
        finally
        {
            await CancelAgentAsync();
            await session.DisposeAsync();
            terminal.WriteOutput("\n\x1b[2m(session ended)\x1b[0m\n");
        }

        async Task CancelAgentAsync()
        {
            if (agentTask is { IsCompleted: false })
            {
                agentCts.Cancel();
                try { await agentTask; }
                catch (OperationCanceledException) { }
                catch { }
            }
            agentCts.Dispose();
        }
    }

    private static void RegisterChatStreamHandler(CopilotSession session, ChatTerminal terminal)
    {
        var inReasoning = false;

        CopilotAdapter.SubscribeToStream(session, evt =>
        {
            switch (evt.Type)
            {
                case "reasoning":
                    if (!inReasoning)
                    {
                        inReasoning = true;
                        terminal.WriteOutput("\x1b[2m\x1b[3m"); // dim italic
                    }
                    terminal.WriteOutput(evt.Content ?? "");
                    break;
                case "delta":
                    if (inReasoning)
                    {
                        inReasoning = false;
                        terminal.WriteOutput("\x1b[0m\n");
                    }
                    terminal.WriteOutput(evt.Content ?? "");
                    break;
                case "tool-start":
                    if (inReasoning)
                    {
                        inReasoning = false;
                        terminal.WriteOutput("\x1b[0m\n");
                    }
                    terminal.WriteOutput($"\n  \x1b[2mtool: {evt.ToolName ?? "?"}\x1b[0m");
                    break;
                case "tool-end":
                    terminal.WriteOutput("\x1b[2m done\x1b[0m\n");
                    break;
                case "error":
                    if (inReasoning)
                    {
                        inReasoning = false;
                        terminal.WriteOutput("\x1b[0m\n");
                    }
                    terminal.WriteOutput($"\n\x1b[31mError: {evt.Content ?? "unknown"}\x1b[0m\n");
                    break;
                case "mcp-loaded":
                    terminal.WriteOutput($"\n\x1b[2mMCP servers: {evt.Content ?? "(none)"}\x1b[0m\n");
                    break;
                case "mcp-status":
                    terminal.WriteOutput($"\n\x1b[2mMCP status: {evt.Content}\x1b[0m\n");
                    break;
            }
        });
    }

    /// <summary>
    /// Splits the terminal into a scrollable output region (top) and a fixed input
    /// line (bottom) using ANSI scroll regions. Agent output writes to the scroll
    /// region without disturbing the input cursor.
    /// </summary>
    private sealed class ChatTerminal : IDisposable
    {
        private readonly int _scrollBottom;
        private readonly int _inputRow;
        private readonly int _width;
        private readonly Channel<string> _outputQueue = Channel.CreateUnbounded<string>();
        private readonly StringBuilder _inputBuffer = new();
        private readonly bool _oldTreatCtrlC;
        private volatile bool _disposed;
        private int _outputRow;
        private int _outputCol;

        public ChatTerminal()
        {
            Console.Clear();
            _oldTreatCtrlC = Console.TreatControlCAsInput;
            Console.TreatControlCAsInput = true;

            var h = Console.WindowHeight;
            _width = Console.WindowWidth;
            _scrollBottom = h - 3;
            _inputRow = h - 1;

            // Set ANSI scroll region (1-indexed)
            Console.Write($"\x1b[1;{_scrollBottom + 1}r");

            // Position output cursor at top of scroll region
            _outputRow = 0;
            _outputCol = 0;
            Console.SetCursorPosition(0, 0);

            // Draw separator
            Console.SetCursorPosition(0, h - 2);
            Console.Write(new string('\u2500', Math.Min(_width, 120)));

            // Draw hint in separator
            const string hint = " /exit \u00b7 /reset ";
            if (_width > hint.Length + 4)
            {
                Console.SetCursorPosition(_width - hint.Length - 1, h - 2);
                Console.Write(hint);
            }

            ShowPrompt();
        }

        public void WriteOutput(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                _outputQueue.Writer.TryWrite(text);
            }
        }

        /// <summary>
        /// Drains queued output onto the scroll region. Only called from
        /// the ReadInput thread so console cursor ops stay single-threaded.
        /// </summary>
        private void FlushOutput()
        {
            if (!_outputQueue.Reader.TryPeek(out _))
            {
                return;
            }

            // Save the input cursor position
            var inputCol = Console.CursorLeft;
            var inputRow = Console.CursorTop;

            // Move to tracked output position
            Console.SetCursorPosition(_outputCol, _outputRow);

            while (_outputQueue.Reader.TryRead(out var text))
            {
                Console.Write(text);
            }

            // Save new output position
            _outputRow = Console.CursorTop;
            _outputCol = Console.CursorLeft;

            // Restore input cursor
            Console.SetCursorPosition(inputCol, inputRow);
        }

        public string? ReadInput()
        {
            _inputBuffer.Clear();
            ShowPrompt();

            while (!_disposed)
            {
                FlushOutput();

                try
                {
                    if (!Console.KeyAvailable)
                    {
                        Thread.Sleep(16);
                        continue;
                    }
                }
                catch (InvalidOperationException)
                {
                    return null;
                }

                ConsoleKeyInfo key;
                try
                {
                    key = Console.ReadKey(intercept: true);
                }
                catch (InvalidOperationException)
                {
                    return null;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    var input = _inputBuffer.ToString();
                    _inputBuffer.Clear();
                    ClearInputLine();
                    return input;
                }

                if (key.Key == ConsoleKey.Escape)
                {
                    _inputBuffer.Clear();
                    ClearInputLine();
                    return null;
                }

                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    return null;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer.Remove(_inputBuffer.Length - 1, 1);
                        RedrawInput();
                    }
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    _inputBuffer.Append(key.KeyChar);
                    RedrawInput();
                }
            }
            return null;
        }

        private void ShowPrompt()
        {
            Console.SetCursorPosition(0, _inputRow);
            Console.Write("> ");
            var visible = GetVisibleInput();
            Console.Write(visible);
            var remaining = Math.Max(0, _width - 2 - visible.Length);
            if (remaining > 0)
            {
                Console.Write(new string(' ', remaining));
            }
            Console.SetCursorPosition(Math.Min(2 + visible.Length, _width - 1), _inputRow);
        }

        private void ClearInputLine()
        {
            Console.SetCursorPosition(0, _inputRow);
            Console.Write("> " + new string(' ', Math.Max(0, _width - 2)));
            Console.SetCursorPosition(2, _inputRow);
        }

        private void RedrawInput()
        {
            Console.SetCursorPosition(2, _inputRow);
            var visible = GetVisibleInput();
            Console.Write(visible + " ");
            var padding = Math.Max(0, _width - 2 - visible.Length - 1);
            if (padding > 0)
            {
                Console.Write(new string(' ', padding));
            }
            Console.SetCursorPosition(Math.Min(2 + visible.Length, _width - 1), _inputRow);
        }

        private string GetVisibleInput()
        {
            var maxVisible = _width - 2;
            if (_inputBuffer.Length <= maxVisible)
            {
                return _inputBuffer.ToString();
            }
            // Show the tail so the cursor stays visible at the end
            return _inputBuffer.ToString(_inputBuffer.Length - maxVisible, maxVisible);
        }

        public void Dispose()
        {
            _disposed = true;
            _outputQueue.Writer.TryComplete();

            // Flush any remaining queued output before tearing down
            Console.SetCursorPosition(_outputCol, _outputRow);
            while (_outputQueue.Reader.TryRead(out var text))
            {
                Console.Write(text);
            }

            Console.Write("\x1b[r"); // reset scroll region
            Console.TreatControlCAsInput = _oldTreatCtrlC;
            Console.SetCursorPosition(0, Console.WindowHeight - 1);
        }
    }
}
