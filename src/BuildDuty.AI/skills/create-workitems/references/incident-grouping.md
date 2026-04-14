# Incident Grouping

Determine which analyses represent the same underlying issue.

---

## Definitions
- **Signal**: A unit of observation (e.g., pipeline run, PR, issue).
- **Analysis**: The interpretation of a signal, or element of a signal, that identifies its cause and supporting evidence.

---

 ## Workflow
 1. For each orphaned analysis (from `list_orphaned_analyses`), load the analysis by its signal ID and analysis ID.
 2. Use the analysis's root cause text and structural evidence (build IDs, pipeline definition IDs, file paths, failing task names, error signatures) as primary correlation criteria.
 3. Cross-reference evidence across orphaned analyses to find causal chains (see below).
 4. Use the signal's `context` field to understand pipeline/repo dependencies and relationships.
 5. Form groups only when causal evidence aligns.

---

 ## Cause vs Symptom
 Group analyses by **shared cause**, not shared symptom.

 - Same symptom ≠ same incident
 - Same cause → same incident (even if symptoms differ)

 **Example:**
 - Multiple pipeline analyses show `artifact not found`
   - If they depend on the same upstream pipeline → same cause, group them
   - If each references different missing artifacts → different causes, keep separate

---

## Correlation Strategy

### Same-Cause Grouping
Group analyses when they describe the same underlying failure, even if they come from different pipelines, stages, or systems.

Evaluate using:

- **Cause**
 Do the analyses describe the same failure mechanism, even if phrased differently?

- **Structural Evidence**
 Do the analyses share concrete identifiers such as:
 - Pipeline definition IDs
 - Stage or task names
 - File paths
 - Error messages or error codes
 - Affected components or services

**Structural evidence strength (highest to lowest):**
1. Exact identifiers (run IDs, build IDs, commit SHAs)
2. Shared artifact or dependency linkage
3. Identical error codes/messages
4. Same file path + task/stage
5. Same component or service

> Prefer higher-confidence structural matches over textual similarity in cause text.

**Examples:**
- Two pipeline analyses show `error NU1301: Unable to load service index` for different NuGet packages → Same NuGet infrastructure issue
- Multiple build phase analyses fail with the same compiler error → Same code defect
- Two analyses reference the same file path and failing task but describe different symptoms → Same root cause
- A GitHub issue analysis reports a general `error NU1301`, while a pipeline analysis logs the same error for a specific package → Same underlying NuGet infrastructure issue

---

 ### Causal Chain Detection
 An analysis may describe a failure that is a **downstream effect** of another analysis's root cause.

Detect causal chains by cross-referencing evidence in the analyses:

- If analysis A references a build number, pipeline name, or run ID that matches analysis B → A is likely downstream of B
- If analysis A reports an artifact download failure and analysis B's signal produces that artifact → A depends on B
- If a PR analysis's failing check matches another pipeline analysis → PR analysis depends on pipeline analysis
- If an issue analysis references a PR or pipeline that matches another analysis → Link them

**Rules:**
- Only form a causal chain when there is **traceable linkage** (IDs, artifacts, explicit references)
- Do NOT infer causality based on timing alone

**Grouping behavior:**
- The **upstream analysis's cause** is the root cause
- Downstream analyses are **symptoms**
- Group all analyses in the chain into the same work item

---

### Cross-Type Correlation
Analyses from different signal types can represent the same incident:

- A GitHub issue analysis tracking a failure + a pipeline analysis showing that failure → Same incident
- A GitHub PR analysis fixing an issue + the issue analysis → Same incident
- An upstream pipeline analysis + downstream dependent pipeline analyses → Same incident

---

## Merge Criteria
Merge analyses only if:

- They share the same **cause**
**OR**
- There is a verified **causal chain** linking them

---

## Do Not Merge On
Do NOT group analyses based solely on:

- Same tests
- Same component, repository, or stage
- Generic wording overlap in cause text
- Temporal proximity (failures occurring at the same time)

---

## Common Misgrouping Pitfalls

- Two pipeline analyses fail with the same error code but in different services with no shared dependency → Do NOT merge
- Multiple test failures in the same repository due to unrelated code paths → Do NOT merge
- Failures occur at the same time after a deployment, but no shared evidence exists → Do NOT merge without causal proof

---

## Grouping Decision Heuristic

Merge analyses if:
- Shared **cause** is established
**OR**
- A **causal chain** is verified

Otherwise:
- Keep analyses separate

---

## Precision Rule
If uncertain, split.

Only merge when there is clear causal evidence.
