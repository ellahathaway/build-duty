// Extension: triage-dashboard
// Canvas for the build-duty triage workflow — displays signals, analysis,
// and incidents for multiple config files with tabbed views.

import { createServer } from "node:http";
import { readFileSync, writeFileSync, mkdirSync, existsSync, readdirSync, statSync } from "node:fs";
import { join, basename, dirname } from "node:path";
import { homedir } from "node:os";
import { joinSession, createCanvas, CanvasError } from "@github/copilot-sdk/extension";

// Persistent storage for saved configs and triage state
const STORAGE_DIR = join(process.env.COPILOT_HOME || join(homedir(), ".copilot"), "extensions", "triage-dashboard", "artifacts");
const CONFIGS_FILE = join(STORAGE_DIR, "configs.json");
const STATE_FILE = join(STORAGE_DIR, "triage-state.json");

function loadSavedConfigs() {
    try {
        if (existsSync(CONFIGS_FILE)) {
            return JSON.parse(readFileSync(CONFIGS_FILE, "utf-8"));
        }
    } catch {}
    return [];
}

function saveSavedConfigs(configs) {
    mkdirSync(STORAGE_DIR, { recursive: true });
    writeFileSync(CONFIGS_FILE, JSON.stringify(configs, null, 2), "utf-8");
}

// Per-instance server state keyed by instanceId.
const servers = new Map();
// Per-config-path triage state — persisted to disk.
const triageState = new Map();

function loadTriageState() {
    try {
        if (existsSync(STATE_FILE)) {
            const data = JSON.parse(readFileSync(STATE_FILE, "utf-8"));
            for (const [key, value] of Object.entries(data)) {
                triageState.set(key, value);
            }
        }
    } catch {}
}

function saveTriageState() {
    try {
        mkdirSync(STORAGE_DIR, { recursive: true });
        const obj = Object.fromEntries(triageState.entries());
        writeFileSync(STATE_FILE, JSON.stringify(obj, null, 2), "utf-8");
    } catch {}
}

// Load persisted state on startup
loadTriageState();

function getOrCreateState(configPath) {
    if (!triageState.has(configPath)) {
        triageState.set(configPath, {
            configPath,
            status: "idle",
            statusMessage: null,
            signals: null,
            findings: null,
            incidents: null,
            lastUpdated: null,
        });
    }
    return triageState.get(configPath);
}

function escapeHtml(str) {
    return String(str)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;");
}

// Search for .yml/.yaml config files in common locations
function searchConfigFiles(query) {
    const results = [];
    const searchDirs = [];

    // Add workspace-relative paths
    if (process.env.COPILOT_WORKSPACE_PATH) {
        searchDirs.push(process.env.COPILOT_WORKSPACE_PATH);
    }
    // Common locations
    const home = homedir();
    const repos = join(home, "Repos");
    if (existsSync(repos)) searchDirs.push(repos);
    const copilotRepos = join(home, ".copilot", "repos");
    if (existsSync(copilotRepos)) searchDirs.push(copilotRepos);

    function walkDir(dir, depth = 0) {
        if (depth > 4 || results.length > 50) return;
        try {
            const entries = readdirSync(dir);
            for (const entry of entries) {
                if (entry.startsWith(".") && entry !== ".build-duty.yml" && !entry.startsWith(".source-build")) continue;
                if (entry === "node_modules" || entry === ".git") continue;
                const fullPath = join(dir, entry);
                try {
                    const stat = statSync(fullPath);
                    if (stat.isDirectory()) {
                        walkDir(fullPath, depth + 1);
                    } else if ((entry.endsWith(".yml") || entry.endsWith(".yaml")) &&
                               (entry.includes("build-duty") || entry.includes("monitor") || entry.includes("triage") ||
                                dir.includes("configs"))) {
                        if (!query || fullPath.toLowerCase().includes(query.toLowerCase()) || entry.toLowerCase().includes(query.toLowerCase())) {
                            results.push(fullPath);
                        }
                    }
                } catch {}
            }
        } catch {}
    }

    for (const dir of searchDirs) {
        walkDir(dir);
    }
    return results;
}

function renderSignalsTable(signals, showInvestigate = true) {
    if (!signals || !signals.length) {
        if (signals && signals.length === 0) return `<p class="muted">✅ All clear — no failures detected across monitored sources.</p>`;
        return `<p class="muted">Awaiting triage run. Click "Run Triage" to collect signals.</p>`;
    }
    const investigateCol = showInvestigate ? `<th></th>` : "";
    let html = `<table><thead><tr><th>Type</th><th>Name</th><th>Branch</th><th>Status</th>${investigateCol}</tr></thead><tbody>`;
    for (const s of signals) {
        const type = s.type || "Unknown";
        const name = s.name || s.title || s.id || "—";
        let url = s.url || s.webUrl || "";
        // Auto-generate URLs from known patterns
        if (!url && s.type === "azdo_build" && s.buildId) {
            url = `https://dev.azure.com/dnceng/internal/_build/results?buildId=${s.buildId}`;
        } else if (!url && s.type === "github_issue" && s.number) {
            const repo = s.repo || "dotnet/dotnet";
            url = `https://github.com/${repo}/issues/${s.number}`;
        } else if (!url && s.type === "github_pr" && s.number) {
            const repo = s.repo || "dotnet/dotnet";
            url = `https://github.com/${repo}/pull/${s.number}`;
        }
        const branch = s.branch || "—";
        const status = s.status || s.result || "—";
        const displayName = s.type === "azdo_build" && s.buildId ? `${name} (${s.buildId})` : s.type === "github_issue" && s.number ? `#${s.number} ${name}` : name;
        const nameCell = url ? `<a href="${escapeHtml(url)}" target="_blank">${escapeHtml(displayName)}</a>` : escapeHtml(displayName);
        const signalData = JSON.stringify(s).replace(/"/g, "&quot;").replace(/'/g, "&#39;");
        const investigateBtn = showInvestigate ? `<td><button class="btn-investigate" onclick='investigate("signal", ${signalData})' title="Investigate">🔍</button></td>` : "";
        html += `<tr><td><span class="badge badge-${type.toLowerCase().replace(/_/g, "")}">${escapeHtml(type)}</span></td><td>${nameCell}</td><td><code>${escapeHtml(branch)}</code></td><td>${escapeHtml(status)}</td>${investigateBtn}</tr>`;
    }
    html += `</tbody></table>`;
    return html;
}

function renderIncidents(incidents, allSignals) {
    if (!incidents || !incidents.length) {
        if (incidents && incidents.length === 0) return `<p class="muted">✅ No incidents — all monitored sources are healthy.</p>`;
        return `<p class="muted">Awaiting triage run. Click "Run Triage" to analyze.</p>`;
    }
    let html = "";
    for (const inc of incidents) {
        const severity = inc.severity || "medium";
        const category = inc.category || "";
        const severityClass = severity === "high" ? "severity-high" : severity === "low" ? "severity-low" : "severity-medium";
        html += `<div class="incident ${severityClass}">`;
        html += `<div class="incident-header">`;
        html += `<span class="severity-indicator severity-${severity}">${severity.toUpperCase()}</span>`;
        if (category) html += ` <span class="badge badge-${category.toLowerCase()}">${escapeHtml(category)}</span>`;
        html += ` <strong>${escapeHtml(inc.title || "Untitled")}</strong>`;
        html += `</div>`;
        if (inc.description) html += `<p class="incident-desc">${escapeHtml(inc.description)}</p>`;
        if (inc.rootCause) html += `<p class="root-cause"><strong>Root cause:</strong> ${escapeHtml(inc.rootCause)}</p>`;
        if (inc.affectedBranches && inc.affectedBranches.length) {
            html += `<div class="affected-branches"><strong>Branches:</strong> ${inc.affectedBranches.map(b => `<code>${escapeHtml(b)}</code>`).join(", ")}</div>`;
        }
        // Show related signals — only use the incident's own signals array (set by triage JSON output)
        const relatedSignals = inc.signals || [];
        if (relatedSignals.length > 0) {
            html += `<details class="signals-dropdown"><summary>${relatedSignals.length} related signal(s)</summary>${renderSignalsTable(relatedSignals, false)}</details>`;
        }
        if (inc.nextSteps) html += `<p class="next-steps"><strong>Next:</strong> ${escapeHtml(inc.nextSteps)}</p>`;
        const incData = JSON.stringify({ ...inc, relatedSignals }).replace(/"/g, "&quot;").replace(/'/g, "&#39;");
        html += `<div class="incident-actions"><button class="btn btn-sm" onclick='investigate("incident", ${incData})'>🔍 Investigate</button></div>`;
        html += `</div>`;
    }
    return html;
}

function renderHtml(instanceId, configs, activeConfigPath) {
    const activeState = activeConfigPath ? getOrCreateState(activeConfigPath) : null;
    const signalCount = activeState?.signals ? activeState.signals.length : 0;
    const incidentCount = activeState?.incidents ? activeState.incidents.length : 0;
    const statusClass = activeState ? (activeState.status === "error" ? "status-error" : activeState.status === "running" ? "status-running" : "status-idle") : "status-idle";

    const configTabs = configs.map((c) => {
        const name = basename(c);
        const isActive = c === activeConfigPath;
        return `<div class="tab ${isActive ? "tab-active" : ""}" onclick="selectConfig('${escapeHtml(c.replace(/\\/g, "\\\\").replace(/'/g, "\\'"))}')">
            <span class="tab-name" title="${escapeHtml(c)}">${escapeHtml(name)}</span>
            <button class="tab-remove" onclick="event.stopPropagation(); removeConfig('${escapeHtml(c.replace(/\\/g, "\\\\").replace(/'/g, "\\'"))}')" title="Remove config">×</button>
        </div>`;
    }).join("");

    const isRunning = activeState?.status === "running";
    const statusMessage = "Collecting signals and analyzing failures…";
    const runningBanner = isRunning ? `
        <div class="banner banner-running">
            <svg class="spinner" viewBox="0 0 16 16" fill="currentColor"><path d="M8 2.5a5.487 5.487 0 0 0-4.131 1.869l1.204 1.204A.25.25 0 0 1 4.896 6H1.25A.25.25 0 0 1 1 5.75V2.104a.25.25 0 0 1 .427-.177l1.38 1.38A7.001 7.001 0 0 1 14.95 7.16a.75.75 0 1 1-1.49.178A5.501 5.501 0 0 0 8 2.5ZM1.705 8.005a.75.75 0 0 1 .834.656 5.501 5.501 0 0 0 9.592 2.97l-1.204-1.204a.25.25 0 0 1 .177-.427h3.646a.25.25 0 0 1 .25.25v3.646a.25.25 0 0 1-.427.177l-1.38-1.38A7.001 7.001 0 0 1 1.05 8.84a.75.75 0 0 1 .656-.834Z"/></svg>
            <span>${escapeHtml(statusMessage)}</span>
        </div>` : "";

    const contentHtml = activeConfigPath ? `
        ${runningBanner}
        <div class="content-header">
            <div>
                <div class="config-path">${escapeHtml(activeConfigPath)}</div>
                <div style="margin-top:6px;">
                    <span class="status-badge ${statusClass}">${escapeHtml(activeState.status)}</span>
                    ${activeState.lastUpdated ? `<span class="timestamp"> · Updated ${escapeHtml(activeState.lastUpdated)}</span>` : ""}
                </div>
            </div>
            <button class="btn" id="refreshBtn" onclick="runTriage()" ${isRunning ? "disabled" : ""}>
                <svg viewBox="0 0 16 16" fill="currentColor"><path d="M8 2.5a5.487 5.487 0 0 0-4.131 1.869l1.204 1.204A.25.25 0 0 1 4.896 6H1.25A.25.25 0 0 1 1 5.75V2.104a.25.25 0 0 1 .427-.177l1.38 1.38A7.001 7.001 0 0 1 14.95 7.16a.75.75 0 1 1-1.49.178A5.501 5.501 0 0 0 8 2.5ZM1.705 8.005a.75.75 0 0 1 .834.656 5.501 5.501 0 0 0 9.592 2.97l-1.204-1.204a.25.25 0 0 1 .177-.427h3.646a.25.25 0 0 1 .25.25v3.646a.25.25 0 0 1-.427.177l-1.38-1.38A7.001 7.001 0 0 1 1.05 8.84a.75.75 0 0 1 .656-.834Z"/></svg>
                ${isRunning ? "Running…" : "Run Triage"}
            </button>
        </div>
        <div class="stats">
            <div class="stat"><div class="stat-value">${signalCount}</div><div class="stat-label">Signals</div></div>
            <div class="stat"><div class="stat-value">${incidentCount}</div><div class="stat-label">Incidents</div></div>
        </div>
        <details class="section-collapse" open><summary><h2>Signals</h2></summary>${renderSignalsTable(activeState.signals)}</details>
        <details class="section-collapse" open><summary><h2>Incidents</h2></summary>${renderIncidents(activeState.incidents, activeState.signals)}</details>
    ` : `<div class="empty-state">
        <p>No config selected. Add a config file to get started.</p>
    </div>`;

    return `<!doctype html>
<html>
<head>
<meta charset="utf-8" />
<title>Triage Dashboard</title>
<style>
* { box-sizing: border-box; margin: 0; padding: 0; }
body {
    font-family: var(--font-sans, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif);
    font-size: var(--text-body-medium, 14px);
    line-height: var(--leading-body-medium, 20px);
    background: var(--background-color-default, #ffffff);
    color: var(--text-color-default, #1f2328);
    padding: 0;
}
h1 {
    font-size: var(--text-title-large, 20px);
    font-weight: var(--font-weight-semibold, 600);
    margin-bottom: 4px;
}
.top-bar {
    display: flex; align-items: center; justify-content: space-between;
    padding: 12px 16px; border-bottom: 1px solid var(--border-color-default, #d1d9e0);
    background: #f6f8fa;
}
.top-bar h1 { margin: 0; }
.top-bar-actions { display: flex; gap: 8px; }
.btn {
    display: inline-flex; align-items: center; gap: 6px;
    padding: 6px 12px; border-radius: 6px; border: 1px solid var(--border-color-default, #d1d9e0);
    background: #ffffff; color: var(--text-color-default, #1f2328);
    font-size: 12px; font-weight: 500; cursor: pointer; transition: background 0.15s;
}
.btn:hover { background: #eaeef2; }
.btn:disabled { opacity: 0.5; cursor: not-allowed; }
.btn svg { width: 14px; height: 14px; }
.btn-primary { background: #2da44e; color: #fff; border-color: #2da44e; }
.btn-primary:hover { background: #298e46; }
.tab-bar {
    display: flex; align-items: stretch; gap: 0; padding: 0 16px;
    border-bottom: 2px solid #d1d9e0;
    background: #ffffff; overflow-x: auto;
}
.tab {
    display: flex; align-items: center; gap: 6px;
    padding: 10px 14px; font-size: 13px; font-weight: 500;
    border-bottom: 3px solid transparent; cursor: pointer;
    color: #656d76; white-space: nowrap;
    margin-bottom: -2px;
    transition: color 0.15s, border-color 0.15s, background 0.15s;
    border-radius: 6px 6px 0 0;
}
.tab:hover { color: #1f2328; background: #f6f8fa; }
.tab-active { color: #0969da; border-bottom-color: #0969da; font-weight: 600; background: #f6f8fa; }
.tab-remove {
    display: inline-flex; align-items: center; justify-content: center;
    width: 18px; height: 18px; border: none; background: transparent;
    color: #656d76; font-size: 15px; line-height: 1;
    border-radius: 4px; cursor: pointer; margin-left: 2px;
}
.tab-remove:hover { background: #ffebe9; color: #cf222e; }
.tab-name { max-width: 180px; overflow: hidden; text-overflow: ellipsis; }
.content { padding: 16px; }
.config-path { font-family: var(--font-mono, monospace); font-size: 12px; color: var(--text-color-muted, #656d76); }
.stats { display: flex; gap: 16px; margin: 12px 0; }
.stat { background: #f6f8fa; border: 1px solid var(--border-color-default, #d1d9e0); border-radius: 6px; padding: 8px 12px; }
.stat-value { font-size: 20px; font-weight: 600; }
.stat-label { font-size: 11px; color: var(--text-color-muted, #656d76); text-transform: uppercase; }
.status-badge { display: inline-block; padding: 2px 8px; border-radius: 12px; font-size: 11px; font-weight: 600; text-transform: uppercase; }
.status-idle { background: #eaeef2; color: #656d76; }
.status-running { background: #ddf4ff; color: #0969da; }
.status-error { background: #ffebe9; color: #cf222e; }
section { margin-bottom: 20px; }
section h2 { font-size: 14px; font-weight: 600; margin-bottom: 8px; color: var(--text-color-default, #1f2328); }
table { width: 100%; border-collapse: collapse; font-size: 13px; }
th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid var(--border-color-default, #d1d9e0); }
th { color: var(--text-color-muted, #656d76); font-weight: 500; font-size: 11px; text-transform: uppercase; }
a { color: #0969da; text-decoration: none; }
a:hover { text-decoration: underline; }
.badge { display: inline-block; padding: 1px 6px; border-radius: 10px; font-size: 11px; font-weight: 500; background: #eaeef2; color: #656d76; }
.badge-azuredevopspipeline, .badge-pipeline, .badge-azdobuild { background: #ddf4ff; color: #0550ae; }
.badge-githubissue, .badge-issue, .badge-githubissue { background: #fbefff; color: #8250df; }
.badge-githubpullrequest, .badge-pr, .badge-githubpr { background: #dafbe1; color: #1a7f37; }
.badge-testfailure { background: #ffebe9; color: #cf222e; }
.badge-buildfailure { background: #ffebe9; color: #cf222e; }
.badge-infrastructure { background: #fff8c5; color: #9a6700; }
.badge-timeout { background: #fff8c5; color: #9a6700; }
.badge-dependency { background: #ddf4ff; color: #0550ae; }
.muted { color: var(--text-color-muted, #656d76); font-style: italic; }
.incident { background: #f6f8fa; border: 1px solid var(--border-color-default, #d1d9e0); border-radius: 6px; padding: 12px; margin-bottom: 8px; }
.incident.severity-high { border-left: 3px solid #cf222e; }
.incident.severity-medium { border-left: 3px solid #bf8700; }
.incident.severity-low { border-left: 3px solid #656d76; }
.incident-header { margin-bottom: 6px; display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
.severity-indicator { font-size: 10px; font-weight: 700; padding: 2px 6px; border-radius: 4px; }
.severity-indicator.severity-high { background: #ffebe9; color: #cf222e; }
.severity-indicator.severity-medium { background: #fff8c5; color: #9a6700; }
.severity-indicator.severity-low { background: #eaeef2; color: #656d76; }
.incident-desc { color: #1f2328; margin: 6px 0; font-size: 13px; line-height: 1.5; }
.root-cause { color: #656d76; margin: 4px 0; font-size: 12px; }
.affected-branches { margin: 6px 0; font-size: 12px; }
.affected-branches code { background: #eaeef2; padding: 1px 5px; border-radius: 4px; font-size: 11px; }
.issue-link { font-size: 12px; color: #8250df; }
.next-steps { margin-top: 6px; font-size: 12px; }
.signals-dropdown { margin-top: 8px; }
.signals-dropdown > summary { cursor: pointer; font-size: 12px; color: #0969da; font-weight: 500; }
.signals-dropdown table { margin-top: 6px; }
.btn-investigate { background: none; border: 1px solid #d0d7de; border-radius: 4px; padding: 2px 6px; cursor: pointer; font-size: 12px; opacity: 0.7; transition: opacity 0.15s; }
.btn-investigate:hover { opacity: 1; background: #f6f8fa; }
.incident-actions { margin-top: 8px; padding-top: 8px; border-top: 1px solid #eee; }
.btn-sm { font-size: 12px; padding: 4px 10px; border-radius: 4px; border: 1px solid #d0d7de; background: #f6f8fa; cursor: pointer; }
.btn-sm:hover { background: #eaeef2; }
.content-header { display: flex; align-items: flex-start; justify-content: space-between; margin-bottom: 4px; }
details { margin-top: 6px; }
summary { cursor: pointer; font-size: 12px; color: #0969da; }
.timestamp { font-size: 11px; color: #656d76; }
.empty-state { text-align: center; padding: 48px 16px; color: #656d76; }
.banner {
    padding: 10px 14px; border-radius: 6px; margin-bottom: 12px;
    font-size: 13px; font-weight: 500; display: flex; align-items: center; gap: 8px;
}
.banner-running { background: #ddf4ff; color: #0550ae; border: 1px solid #a8d4f5; }
.banner svg { width: 14px; height: 14px; flex-shrink: 0; }
.spinner { animation: spin 1s linear infinite; }
@keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }
.section-collapse { margin-bottom: 20px; }
.section-collapse > summary { cursor: pointer; list-style: none; display: flex; align-items: center; gap: 6px; margin-bottom: 8px; }
.section-collapse > summary::-webkit-details-marker { display: none; }
.section-collapse > summary::before { content: "▶"; font-size: 10px; color: #656d76; transition: transform 0.15s; }
.section-collapse[open] > summary::before { transform: rotate(90deg); }
.section-collapse > summary h2 { font-size: 14px; font-weight: 600; color: #1f2328; margin: 0; }
/* Add config modal */
.modal-overlay {
    display: none; position: fixed; top: 0; left: 0; right: 0; bottom: 0;
    background: rgba(0,0,0,0.3); z-index: 100; align-items: center; justify-content: center;
}
.modal-overlay.visible { display: flex; }
.modal {
    background: #fff; border-radius: 12px; padding: 24px; width: 90%; max-width: 500px;
    box-shadow: 0 8px 24px rgba(0,0,0,0.15); border: 1px solid #d1d9e0;
}
.modal h2 { font-size: 16px; margin-bottom: 12px; }
.modal-input {
    width: 100%; padding: 8px 12px; border: 1px solid #d1d9e0; border-radius: 6px;
    font-size: 13px; font-family: var(--font-mono, monospace); margin-bottom: 8px;
}
.modal-input:focus { outline: none; border-color: #0969da; box-shadow: 0 0 0 3px rgba(9,105,218,0.15); }
.search-results {
    max-height: 200px; overflow-y: auto; border: 1px solid #d1d9e0; border-radius: 6px;
    margin-bottom: 12px; display: none;
}
.search-results.visible { display: block; }
.search-result {
    padding: 8px 12px; cursor: pointer; font-size: 12px; font-family: monospace;
    border-bottom: 1px solid #eaeef2; color: #1f2328;
}
.search-result:hover { background: #ddf4ff; }
.search-result:last-child { border-bottom: none; }
.modal-actions { display: flex; gap: 8px; justify-content: flex-end; margin-top: 12px; }
</style>
</head>
<body>
<div class="top-bar">
    <h1>Triage Dashboard</h1>
    <div class="top-bar-actions">
        <button class="btn btn-primary" onclick="showAddConfig()">
            <svg viewBox="0 0 16 16" fill="currentColor"><path d="M7.75 2a.75.75 0 0 1 .75.75V7h4.25a.75.75 0 0 1 0 1.5H8.5v4.25a.75.75 0 0 1-1.5 0V8.5H2.75a.75.75 0 0 1 0-1.5H7V2.75A.75.75 0 0 1 7.75 2Z"/></svg>
            Add Config
        </button>
    </div>
</div>

${configs.length > 0 ? `<div class="tab-bar">${configTabs}</div>` : ""}

<div class="content">
    ${contentHtml}
</div>

<!-- Add Config Modal -->
<div class="modal-overlay" id="addConfigModal">
    <div class="modal">
        <h2>Add Config File</h2>
        <p style="font-size:12px; color:#656d76; margin-bottom:12px;">Enter the full path to a .build-duty.yml config file, or search below.</p>
        <input class="modal-input" id="configInput" type="text" placeholder="C:\\path\\to\\configs\\.build-duty.yml" oninput="onSearchInput()" />
        <div class="search-results" id="searchResults"></div>
        <div class="modal-actions">
            <button class="btn" onclick="hideAddConfig()">Cancel</button>
            <button class="btn btn-primary" onclick="addConfig()">Add</button>
        </div>
    </div>
</div>

<script>
const evtSource = new EventSource("/events");
evtSource.onmessage = () => { window.location.reload(); };

async function runTriage() {
    const btn = document.getElementById("refreshBtn");
    btn.disabled = true;
    btn.innerHTML = "Running…";
    try {
        const res = await fetch("/refresh", { method: "POST" });
        if (!res.ok) btn.innerHTML = "Error";
    } catch { btn.innerHTML = "Error"; }
}

function investigate(type, data) {
    fetch("/investigate", { method: "POST", headers: {"Content-Type":"application/json"}, body: JSON.stringify({ type, data }) });
}

function selectConfig(path) {
    fetch("/select-config", { method: "POST", headers: {"Content-Type":"application/json"}, body: JSON.stringify({configPath: path}) })
        .then(() => window.location.reload());
}

function removeConfig(path) {
    if (!confirm("Remove this config from the dashboard?")) return;
    fetch("/remove-config", { method: "POST", headers: {"Content-Type":"application/json"}, body: JSON.stringify({configPath: path}) })
        .then(() => window.location.reload());
}

function showAddConfig() {
    document.getElementById("addConfigModal").classList.add("visible");
    document.getElementById("configInput").focus();
}

function hideAddConfig() {
    document.getElementById("addConfigModal").classList.remove("visible");
    document.getElementById("searchResults").classList.remove("visible");
}

let searchTimeout;
function onSearchInput() {
    clearTimeout(searchTimeout);
    const q = document.getElementById("configInput").value;
    if (q.length < 2) { document.getElementById("searchResults").classList.remove("visible"); return; }
    searchTimeout = setTimeout(async () => {
        const res = await fetch("/search-configs?q=" + encodeURIComponent(q));
        const results = await res.json();
        const container = document.getElementById("searchResults");
        if (results.length === 0) { container.classList.remove("visible"); return; }
        container.innerHTML = results.map(r => \`<div class="search-result" onclick="pickSearchResult('\${r.replace(/\\\\/g, "\\\\\\\\").replace(/'/g, "\\\\'")}')">\${r}</div>\`).join("");
        container.classList.add("visible");
    }, 300);
}

function pickSearchResult(path) {
    document.getElementById("configInput").value = path;
    document.getElementById("searchResults").classList.remove("visible");
}

function addConfig() {
    const path = document.getElementById("configInput").value.trim();
    if (!path) return;
    fetch("/add-config", { method: "POST", headers: {"Content-Type":"application/json"}, body: JSON.stringify({configPath: path}) })
        .then(() => { hideAddConfig(); window.location.reload(); });
}

// Close modal on backdrop click
document.getElementById("addConfigModal").addEventListener("click", (e) => {
    if (e.target === e.currentTarget) hideAddConfig();
});
</script>
</body>
</html>`;
}

async function startServer(instanceId) {
    const clients = new Set();
    // Per-instance state: list of configs and which is active
    const instanceState = {
        configs: loadSavedConfigs(),
        activeConfig: null,
    };
    // Set active to first config if available
    if (instanceState.configs.length > 0) {
        instanceState.activeConfig = instanceState.configs[0];
    }

    const server = createServer(async (req, res) => {
        if (req.url === "/events") {
            res.writeHead(200, {
                "Content-Type": "text/event-stream",
                "Cache-Control": "no-cache",
                Connection: "keep-alive",
            });
            clients.add(res);
            req.on("close", () => clients.delete(res));
            return;
        }

        // Parse JSON body helper
        const parseBody = () => new Promise((resolve) => {
            let body = "";
            req.on("data", (chunk) => { body += chunk; });
            req.on("end", () => { try { resolve(JSON.parse(body)); } catch { resolve({}); } });
        });

        if (req.url === "/refresh" && req.method === "POST") {
            res.writeHead(202, { "Content-Type": "application/json" });
            res.end(JSON.stringify({ ok: true }));
            if (instanceState.activeConfig) {
                const configName = basename(instanceState.activeConfig);
                session.send(
                    `Create a new session named "Triage: ${configName}" and run triage for config path: ${instanceState.activeConfig}. ` +
                    `Use the kickoff_prompt: "/triage with config path: ${instanceState.activeConfig} — output as JSON" in autopilot mode. ` +
                    `When the triage session completes, parse the JSON output (the fenced json code block) and push the "signals" array via update_signals and the "incidents" array via update_incidents to triage-dashboard canvas instance "${instanceId}" with configPath "${instanceState.activeConfig}".`
                );
            }
            return;
        }

        if (req.url === "/investigate" && req.method === "POST") {
            const body = await parseBody();
            res.writeHead(202, { "Content-Type": "application/json" });
            res.end(JSON.stringify({ ok: true }));
            let investigatePrompt = "";
            let sessionName = "Investigation";
            if (body.type === "incident") {
                const inc = body.data;
                const signalsContext = inc.relatedSignals ? `\n\nRelated signals:\n${JSON.stringify(inc.relatedSignals, null, 2)}` : "";
                sessionName = `Investigate: ${inc.title || "incident"}`.slice(0, 40);
                investigatePrompt =
                    `Investigate this incident and provide detailed next steps:\n\n` +
                    `**${inc.title}** (${inc.severity} severity)\n` +
                    `${inc.description || ""}\n` +
                    `Affected branches: ${(inc.affectedBranches || []).join(", ")}\n` +
                    `${inc.rootCause ? `Root cause: ${inc.rootCause}\n` : ""}` +
                    signalsContext;
            } else if (body.type === "signal") {
                const sig = body.data;
                const url = sig.url || sig.webUrl || (sig.buildId ? `https://dev.azure.com/dnceng/internal/_build/results?buildId=${sig.buildId}` : "");
                sessionName = `Investigate: ${(sig.title || sig.name || sig.pipeline || "signal").slice(0, 30)}`;
                investigatePrompt =
                    `Investigate this signal and determine root cause and next steps:\n\n` +
                    `**${sig.title || sig.name || sig.id}** (${sig.type})\n` +
                    `Branch: ${sig.branch || "unknown"}\n` +
                    `Status: ${sig.status || sig.result || "unknown"}\n` +
                    `${url ? `URL: ${url}\n` : ""}` +
                    `${sig.buildId ? `Build ID: ${sig.buildId}\n` : ""}` +
                    `${sig.pipeline ? `Pipeline: ${sig.pipeline}\n` : ""}` +
                    `${sig.number ? `Issue/PR: #${sig.number} in ${sig.repo || "dotnet/dotnet"}\n` : ""}`;
            }
            if (investigatePrompt) {
                session.send(
                    `Create a new session named "${sessionName}" with the kickoff_prompt below in autopilot mode. ` +
                    `Report findings back to me when complete.\n\n` +
                    `Kickoff prompt:\n${investigatePrompt}`
                );
            }
            return;
        }

        if (req.url === "/add-config" && req.method === "POST") {
            const { configPath } = await parseBody();
            if (configPath && !instanceState.configs.includes(configPath)) {
                instanceState.configs.push(configPath);
                saveSavedConfigs(instanceState.configs);
                instanceState.activeConfig = configPath;
                getOrCreateState(configPath);
            }
            res.writeHead(200, { "Content-Type": "application/json" });
            res.end(JSON.stringify({ ok: true }));
            return;
        }

        if (req.url === "/remove-config" && req.method === "POST") {
            const { configPath } = await parseBody();
            instanceState.configs = instanceState.configs.filter((c) => c !== configPath);
            saveSavedConfigs(instanceState.configs);
            if (instanceState.activeConfig === configPath) {
                instanceState.activeConfig = instanceState.configs[0] || null;
            }
            res.writeHead(200, { "Content-Type": "application/json" });
            res.end(JSON.stringify({ ok: true }));
            return;
        }

        if (req.url === "/select-config" && req.method === "POST") {
            const { configPath } = await parseBody();
            if (configPath && instanceState.configs.includes(configPath)) {
                instanceState.activeConfig = configPath;
            }
            res.writeHead(200, { "Content-Type": "application/json" });
            res.end(JSON.stringify({ ok: true }));
            return;
        }

        if (req.url?.startsWith("/search-configs")) {
            const url = new URL(req.url, "http://localhost");
            const q = url.searchParams.get("q") || "";
            const results = searchConfigFiles(q);
            res.writeHead(200, { "Content-Type": "application/json" });
            res.end(JSON.stringify(results));
            return;
        }

        res.setHeader("Content-Type", "text/html; charset=utf-8");
        res.end(renderHtml(instanceId, instanceState.configs, instanceState.activeConfig));
    });
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    const address = server.address();
    const port = typeof address === "object" && address ? address.port : 0;
    return { server, url: `http://127.0.0.1:${port}/`, clients, instanceState };
}

function notifyClients(instanceId) {
    const entry = servers.get(instanceId);
    if (!entry) return;
    for (const client of entry.clients) {
        client.write(`data: refresh\n\n`);
    }
}

const session = await joinSession({
    canvases: [
        createCanvas({
            id: "triage-dashboard",
            displayName: "Triage Dashboard",
            description: "Displays build-duty triage results — signals, analysis findings, and incidents for a given config file.",
            inputSchema: {
                type: "object",
                properties: {
                    configPath: {
                        type: "string",
                        description: "Optional path to a .build-duty.yml config to auto-select on open.",
                    },
                },
            },
            actions: [
                {
                    name: "update_signals",
                    description: "Push collected signals data to the dashboard for display.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            signals: {
                                type: "array",
                                description: "Array of signal objects from build_duty_collect_signals.",
                                items: { type: "object" },
                            },
                            configPath: { type: "string", description: "Optional config path to update. Uses active config if omitted." },
                        },
                        required: ["signals"],
                    },
                    handler: async (ctx) => {
                        const entry = servers.get(ctx.instanceId);
                        if (!entry) throw new CanvasError("not_open", "Canvas instance not open.");
                        const configPath = ctx.input.configPath || entry.instanceState.activeConfig;
                        if (!configPath) throw new CanvasError("no_config", "No active config selected.");
                        // Auto-add config if not present
                        if (!entry.instanceState.configs.includes(configPath)) {
                            entry.instanceState.configs.push(configPath);
                            saveSavedConfigs(entry.instanceState.configs);
                            entry.instanceState.activeConfig = configPath;
                        }
                        const state = getOrCreateState(configPath);
                        state.signals = ctx.input.signals;
                        state.status = "idle";
                        state.lastUpdated = new Date().toLocaleTimeString();
                        saveTriageState();
                        notifyClients(ctx.instanceId);
                        return { signalCount: state.signals.length };
                    },
                },
                {
                    name: "update_incidents",
                    description: "Push reconciled incident data to the dashboard for display.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            incidents: {
                                type: "array",
                                description: "Array of incident objects from reconcile-findings.",
                                items: { type: "object" },
                            },
                            configPath: { type: "string", description: "Optional config path to update. Uses active config if omitted." },
                        },
                        required: ["incidents"],
                    },
                    handler: async (ctx) => {
                        const entry = servers.get(ctx.instanceId);
                        if (!entry) throw new CanvasError("not_open", "Canvas instance not open.");
                        const configPath = ctx.input.configPath || entry.instanceState.activeConfig;
                        if (!configPath) throw new CanvasError("no_config", "No active config selected.");
                        // Auto-add config if not present
                        if (!entry.instanceState.configs.includes(configPath)) {
                            entry.instanceState.configs.push(configPath);
                            saveSavedConfigs(entry.instanceState.configs);
                            entry.instanceState.activeConfig = configPath;
                        }
                        const state = getOrCreateState(configPath);
                        state.incidents = ctx.input.incidents;
                        state.status = "idle";
                        state.lastUpdated = new Date().toLocaleTimeString();
                        saveTriageState();
                        notifyClients(ctx.instanceId);
                        return { incidentCount: state.incidents.length };
                    },
                },
                {
                    name: "set_status",
                    description: "Update the dashboard status indicator (idle, running, error). Accepts a message for progress updates during triage steps.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            status: { type: "string", enum: ["idle", "running", "error"] },
                            configPath: { type: "string", description: "Optional config path to set status for. Uses active config if omitted." },
                            message: { type: "string", description: "Progress message shown in the running banner. E.g. 'Step 1/3: Collecting signals…'" },
                        },
                        required: ["status"],
                    },
                    handler: async (ctx) => {
                        const entry = servers.get(ctx.instanceId);
                        if (!entry) throw new CanvasError("not_open", "Canvas instance not open.");
                        const configPath = ctx.input.configPath || entry.instanceState.activeConfig;
                        if (!configPath) throw new CanvasError("no_config", "No active config selected.");
                        // Auto-add config if not present
                        if (!entry.instanceState.configs.includes(configPath)) {
                            entry.instanceState.configs.push(configPath);
                            saveSavedConfigs(entry.instanceState.configs);
                            entry.instanceState.activeConfig = configPath;
                        }
                        const state = getOrCreateState(configPath);
                        state.status = ctx.input.status;
                        state.statusMessage = ctx.input.message || null;
                        state.lastUpdated = new Date().toLocaleTimeString();
                        saveTriageState();
                        notifyClients(ctx.instanceId);
                        return { status: state.status };
                    },
                },
                {
                    name: "add_config",
                    description: "Add a config file to the dashboard.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            configPath: { type: "string", description: "Full path to the .build-duty.yml config file." },
                        },
                        required: ["configPath"],
                    },
                    handler: async (ctx) => {
                        const entry = servers.get(ctx.instanceId);
                        if (!entry) throw new CanvasError("not_open", "Canvas instance not open.");
                        const configPath = ctx.input.configPath;
                        if (!entry.instanceState.configs.includes(configPath)) {
                            entry.instanceState.configs.push(configPath);
                            saveSavedConfigs(entry.instanceState.configs);
                        }
                        entry.instanceState.activeConfig = configPath;
                        getOrCreateState(configPath);
                        notifyClients(ctx.instanceId);
                        return { configs: entry.instanceState.configs };
                    },
                },
                {
                    name: "remove_config",
                    description: "Remove a config file from the dashboard.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            configPath: { type: "string", description: "Full path to the config file to remove." },
                        },
                        required: ["configPath"],
                    },
                    handler: async (ctx) => {
                        const entry = servers.get(ctx.instanceId);
                        if (!entry) throw new CanvasError("not_open", "Canvas instance not open.");
                        entry.instanceState.configs = entry.instanceState.configs.filter((c) => c !== ctx.input.configPath);
                        saveSavedConfigs(entry.instanceState.configs);
                        if (entry.instanceState.activeConfig === ctx.input.configPath) {
                            entry.instanceState.activeConfig = entry.instanceState.configs[0] || null;
                        }
                        // Clear persisted triage state for this config
                        triageState.delete(ctx.input.configPath);
                        saveTriageState();
                        notifyClients(ctx.instanceId);
                        return { configs: entry.instanceState.configs };
                    },
                },
                {
                    name: "get_state",
                    description: "Get the current triage state for this dashboard instance.",
                    handler: async (ctx) => {
                        const entry = servers.get(ctx.instanceId);
                        if (!entry) throw new CanvasError("not_open", "Canvas instance not open.");
                        const configPath = entry.instanceState.activeConfig;
                        const state = configPath ? getOrCreateState(configPath) : null;
                        return {
                            configs: entry.instanceState.configs,
                            activeConfig: configPath,
                            status: state?.status || "idle",
                            signalCount: state?.signals ? state.signals.length : 0,
                            incidentCount: state?.incidents ? state.incidents.length : 0,
                            lastUpdated: state?.lastUpdated,
                        };
                    },
                },
            ],
            open: async (ctx) => {
                let entry = servers.get(ctx.instanceId);
                if (!entry) {
                    entry = await startServer(ctx.instanceId);
                    servers.set(ctx.instanceId, entry);
                }

                // If a configPath was provided, add it and make it active
                const configPath = ctx.input?.configPath;
                if (configPath) {
                    if (!entry.instanceState.configs.includes(configPath)) {
                        entry.instanceState.configs.push(configPath);
                        saveSavedConfigs(entry.instanceState.configs);
                    }
                    entry.instanceState.activeConfig = configPath;
                    getOrCreateState(configPath);
                }

                const activeName = entry.instanceState.activeConfig
                    ? basename(entry.instanceState.activeConfig)
                    : "No config";

                return {
                    title: `Triage: ${activeName}`,
                    url: entry.url,
                };
            },
            onClose: async (ctx) => {
                const entry = servers.get(ctx.instanceId);
                if (entry) {
                    for (const client of entry.clients) {
                        client.end();
                    }
                    servers.delete(ctx.instanceId);
                    await new Promise((resolve) => entry.server.close(() => resolve()));
                }
            },
        }),
    ],
});
