# Project Specification: Local Software Factory & Agent Orchestrator

## 1. Objective & Operating Model
Build a full-stack C# (.NET 9) Agent Orchestrator and Blazor Web UI that acts as an automated local software factory. 
- **Upfront Input:** The user provides high-level Architecture & Technical Specifications (authored via external frontier models) alongside a target local Git repository path.
- **Single-Model Local Execution:** All local agent stages MUST route to a single locally hosted **Qwen 3.8** model served via LM Studio (`http://localhost:1234/v1`) or Ollama (`http://localhost:11434/v1`) using the OpenAI-compatible Chat Completions API.
- **No Model Swapping:** Do not swap models in memory. Instead, differentiate agent roles using system prompts, structured JSON schemas, temperature adjustments, and Qwen 3.8's `reasoning_effort` parameter (`low`, `medium`, `xhigh`).

---

## 2. Technology Stack & Solution Structure
Create a clean .NET 9 solution (`LocalSoftwareFactory.sln`) with the following projects:
1. **`Factory.Contracts` (Class Library):**
   - Domain models, DTOs, state machine enums, and strict JSON Schema definitions for LLM structured outputs.
2. **`Factory.Engine` (Class Library):**
   - **LLM Client:** Custom `HttpClient` wrapper or `Microsoft.Extensions.AI` / `SemanticKernel` connector targeting the local OpenAI-compatible `/v1/chat/completions` endpoint. Must support streaming (SSE), custom request properties (`reasoning_effort`, `preserve_thinking`), and `response_format: { type: "json_schema" }`.
   - **Workspace & Git Manager:** Wrapper around `LibGit2Sharp` or CLI `git` via `System.Diagnostics.Process` to create feature branches, stage files, generate unified diffs, commit, and rollback.
   - **Deterministic Build/Test Runner:** CLI execution service (`System.Diagnostics.Process`) that runs build and test commands (e.g., `dotnet build`, `dotnet test`, `npm test`) in the target repo with timeout enforcement and stdout/stderr capture.
   - **Orchestrator State Machine:** Executes the 5-stage pipeline asynchronously and emits real-time events via C# events / `System.Threading.Channels`.
3. **`Factory.Web` (Blazor Web App - Interactive Server):**
   - Real-time dashboard using SignalR / Blazor Server state notifications.
   - SQLite database (via EF Core) to persist Runs, Tasks, Subtasks, Agent Logs, and Diffs.
4. **`Factory.Tests` (xUnit):**
   - Unit tests for state transitions, JSON schema deserialization, and workspace file patching.

---

## 3. Single-Model Qwen 3.8 Configuration Strategy
Implement an `AgentProfile` configuration object passed to the single local model endpoint per stage:

| Pipeline Stage | Agent Role | `reasoning_effort` | Temp | Output Format | Purpose |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **1. Decompose** | Task Planner | `xhigh` | `0.6` | Strict JSON (`TaskGraph`) | Break input Architecture Spec into ordered, atomic coding subtasks with target file paths. |
| **2. Refine** | Context Specifier | `medium` | `0.4` | Strict JSON (`RefinedSpec`) | Inspect existing repo files for the subtask; define exact method signatures, interfaces, and test cases. |
| **3. Build** | Code Generator | `medium` | `0.2` | Strict JSON (`FilePatchSet`) | Write implementation code and unit tests for the current subtask. |
| **4. Review** | Static Reviewer | `xhigh` | `0.3` | Strict JSON (`ReviewVerdict`) | Review generated git diff + compiler/test output against the original subtask spec. |
| **5. Fixer** | Bug Fixer | `xhigh` | `0.2` | Strict JSON (`FilePatchSet`) | Triggered ONLY if Build/Test CLI fails or Reviewer rejects. Receives compiler stderr/failed test logs. |

*Note: For LM Studio / Ollama compatibility, follow Qwen 3.8 sampling best practices (`top_p: 0.95`, `top_k: 20` when thinking is active; `top_p: 0.80` when instruct-only).*

---

## 4. Deterministic Pipeline Workflow (The Factory Loop)

The `FactoryOrchestrator` service must execute the following state machine for each work item:

1. **Ingest & Context Gathering:**
   - User selects a local Git repository path and pastes/uploads the `ArchitectureSpec.md`.
   - Engine scans the repository file tree (ignoring `.git`, `bin`, `obj`, `node_modules`) to create a lightweight repository map.
2. **Stage 1: Decomposition (`Decompose`)**
   - Send `ArchitectureSpec.md` + Repo Map to Qwen 3.8 (`Task Planner` profile).
   - Parse returned JSON into an ordered list of `Subtask` entities.
   - **UI Gate (Optional Pause):** Allow user to approve, reorder, or edit subtasks in the UI before starting execution.
3. **Stage 2: Subtask Refinement (`Refine`)**
   - For the active `Subtask`, read the contents of relevant existing files identified in Stage 1.
   - Send `Subtask` + existing file contents to Qwen 3.8 (`Context Specifier` profile) to produce exact implementation instructions and edge-case checks.
4. **Stage 3: Code Generation (`Build`)**
   - Checkout/ensure a dedicated working branch (e.g., `factory/run-{id}`).
   - Send the `RefinedSpec` to Qwen 3.8 (`Code Generator` profile).
   - Apply the returned file writes/modifications to disk inside the target repository.
5. **Stage 4: Deterministic Build & Test (`Verify`)**
   - Execute the configured CLI verification command (e.g., `dotnet build --no-incremental && dotnet test`).
   - **If CLI Fails (Exit Code != 0):** Route stdout/stderr directly to the **Fixer Agent** (max 3 retry loops). Do NOT invoke the LLM Reviewer until the code compiles and tests pass.
6. **Stage 5: Agent Code Review (`Review`)**
   - Once CLI build/tests pass, run `git diff` to capture exact changes.
   - Send `RefinedSpec` + `git diff` + Test Summary to Qwen 3.8 (`Static Reviewer` profile).
   - **If Approved (`passed: true`):** Stage and commit changes (`git commit -m "feat(subtask): ..."`), mark Subtask `Completed`, and advance to the next Subtask.
   - **If Rejected (`passed: false`):** Send reviewer feedback to the **Fixer Agent** and re-run Stage 4 & 5 (counting toward the max retry cap). If max retries are exceeded, pause the pipeline and flag for human intervention in the UI.
7. **Stage 6: Integration**
   - When all subtasks complete, run a final full-solution build/test check and present a summary with a one-click merge or PR preparation option.

---

## 5. Required JSON Schemas (`Factory.Contracts`)

Enforce these schemas via `response_format` so the C# orchestrator never fails on markdown chatter:

```csharp
public record TaskGraphResponse(List<SubtaskDto> Subtasks);

public record SubtaskDto(
    int Order,
    string Title,
    string Description,
    List<string> TargetFilesToModify,
    List<string> TargetFilesToCreate,
    List<string> ContextFilesToRead,
    string VerificationCommand);

public record FilePatchSetResponse(
    string SummaryOfChanges,
    List<FileOperationDto> Operations);

public record FileOperationDto(
    string RelativeFilePath,
    string Action, // "CREATE", "OVERWRITE", "DELETE"
    string FileContent);

public record ReviewVerdictResponse(
    bool Passed,
    int ConfidenceScore,
    List<string> BlockingIssues,
    List<string> Suggestions);