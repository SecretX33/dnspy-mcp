# AGENT.md — dnSpy MCP Server (C#)

This document explains **every major aspect** of this MCP server so another agent (or human) can operate, extend, and troubleshoot it safely.

---

## 1) Purpose

`dnspy-mcp` is a local MCP (Model Context Protocol) server for .NET reverse engineering.

It provides tools to:
- inspect assemblies (`list_types`, `list_methods`, `search_members`)
- decompile code (`decompile_type`, `decompile_method`)
- inspect IL (`get_method_il`, `find_string_references`)
- generate dnSpy navigation instructions (`format_dnspy_jump`)
- patch binaries (`patch_replace_string_literal`, `patch_nop_instructions`)
- rename symbols (`rename_type`, `rename_method`, `rename_namespace`)
- assemble IL (`set_function_opcodes`, `overwrite_method_body`)
- recompile a method from C# (`update_method_source`)

It does **not** require the dnSpy app at runtime. It uses:
- `dnlib`
- `ICSharpCode.Decompiler`
- `Microsoft.CodeAnalysis.CSharp` (Roslyn, for `update_method_source`)

---

## 2) MCP transport + protocol

Transport: **stdio JSON-RPC**.

Supported framing:
- LSP-style headers: `Content-Length: N\r\n\r\n<json>`
- line framed JSON (single JSON message per line)

Implemented MCP methods:
- `initialize`
- `notifications/initialized`
- `ping`
- `tools/list`
- `tools/call`
- `resources/list`
- `resources/read`

Protocol version used: `2024-11-05`.

---

## 3) Project layout

- `src/DnSpyMcpServer/Program.cs`
  - app entrypoint
- `Core/DnSpyMcpHost.cs`
  - dependency wiring (rpc, registry, services)
- `Core/McpServer.cs`
  - request router (`initialize`, `tools/*`, `resources/*`)
- `Transport/StdioJsonRpc.cs`
  - stdio read/write + framing logic
- `Tools/*`
  - attribute-based tool registration and tool implementations
- `Services/AssemblyAnalysisService.cs`
  - decompilation, searching, IL extraction, patching
- `Services/ResourceRegistry.cs`
  - MCP resources implementation

---

## 4) Tool registration model

Tools are discovered via reflection:
- methods in `Tools/DnSpyTools.cs`
- marked with `[McpTool(name, description)]`
- parameters described with `[ToolParam(description)]`

`ToolRegistry` generates MCP JSON schema from C# signatures.

First parameter of every tool must be `ToolContext`.

---

## 5) Detailed tool reference

### Analysis / navigation tools

1. `list_types`
2. `decompile_type`
3. `decompile_method`
4. `get_method_il`
5. `search_members`
6. `list_methods`
7. `find_string_references`
8. `format_dnspy_jump`

Token-rich output is intentional:
- includes `TypeDef`, `MethodDef`, etc.
- allows direct jump in dnSpy or precise patch target selection

### Patch tools

9. `patch_replace_string_literal`
- target by `methodDefToken` + `ilOffset`
- replaces string literal operand at that IL instruction

10. `patch_nop_instructions`
- target by `methodDefToken` + `ilOffset`
- NOPs `count` instructions from that offset

### Refactor / write tools

Target a method by `typeFullName` + `methodName` (+ optional `parameterTypeNames`), matching `decompile_method` and `get_method_il`, so a read-then-write flow uses the same identifiers.

11. `rename_type`
- set `TypeDef.Name`, optionally `TypeDef.Namespace` via `newNamespace`

12. `rename_method`
- set `MethodDef.Name`, overload-aware

13. `rename_namespace`
- set `Namespace` on every `TypeDef` in `oldNamespace`

14. `set_function_opcodes`
- assemble `ilOpcodes` and edit at a 0-based `index`
- `mode = Overwrite` replaces from the index, `Append` inserts

15. `overwrite_method_body`
- clear the body and rebuild it entirely from `ilOpcodes`

16. `update_method_source`
- compile a full C# method with Roslyn, then splice its IL into the target
- references to the target assembly's own types are resolved to local definitions; only external/BCL references are imported

IL assembler operand support (`set_function_opcodes`, `overwrite_method_body`): no-operand opcodes, `ldstr`, `ldc.i4/i8/r4/r8`, `call`/`callvirt`/`newobj` (as `Type::Method(ParamType, ...)`), `ld`/`st` fields (as `Type::Field`), type operands, and branch targets addressed by 0-based instruction index. Operands naming the target's own types bind locally; external framework types like `System.Console` also resolve. Anything else fails loudly rather than emitting a wrong instruction. A partial `Overwrite` that would orphan an existing branch/switch target is rejected; use `overwrite_method_body` to rebuild the whole body instead.

`update_method_source` limits: the declared method name must match the target, the full signature must line up (static/instance, parameter count and types, return type), and only imperative bodies are supported (no lambdas, async, iterators, or access to the target type's own private members, since these need generated helper types that do not exist in the target). Ambiguous overload calls and composed types over the target's own types are rejected rather than mis-bound.

#### Patch safety rule

**Backups are always created before any write**.

For each write call:
- source file is copied to `sourcePath.yyyyMMdd_HHmmss.bak` (a numeric suffix is added if that name already exists)
- the change is then written to the destination

Rename and IL/source rewrites can break strong-name/signing expectations and, for renames, do not fix reflection or string-based lookups. In-place writes overwrite the original (a `.bak` is still created first) and the cache is invalidated so later reads in the same session reflect the change; use the default `*.patched` output when you want the original left untouched.

Destination behavior:
- if `inPlace = true`: destination = source file
- else destination = `outputPath` if provided, or `<name>.patched.<ext>`

---

## 6) Resources

`resources/list`:
- `dnspy://assemblies`
- per-cached-assembly resources for summary/types

`resources/read` supports:
- `dnspy://assemblies`
- `dnspy://assembly?path=<...>&view=summary`
- `dnspy://assembly?path=<...>&view=types`

Cache is populated when tools load assemblies by `assemblyPath`.

---

## 7) Typical agent workflows

### A) Locate popup string, then jump in dnSpy

1) `find_string_references` with literal fragment
2) take returned `MethodDef`, `IL_xxxx`
3) `format_dnspy_jump` using those tokens/offset

### B) Patch popup text

1) find `MethodDef` + `IL` line with literal
2) call `patch_replace_string_literal`
3) inspect output path + backup path
4) validate by re-running `find_string_references` on patched file

### C) Suppress call path (advanced)

1) locate call-site in IL
2) call `patch_nop_instructions` with suitable `count`
3) verify control flow manually in dnSpy

---

## 8) Build, run, publish

Build:
```bash
dotnet build src/DnSpyMcpServer/DnSpyMcpServer.csproj -c Release
```

Run:
```bash
dotnet run --project src/DnSpyMcpServer/DnSpyMcpServer.csproj -c Release
```

Publish profiles:
- `win-x64`
- `linux-x64`

---

## 9) OpenCode config (Windows)

Config file:
- `%USERPROFILE%\.config\opencode\opencode.json`

Use local command format:
```json
"dnspy": {
  "type": "local",
  "enabled": true,
  "command": [
    "dotnet",
    "C:/.../DnSpyMcpServer.dll"
  ]
}
```

---

## 10) Known caveats

- Patching can break signatures/strong-name expectations in some apps.
- NOP patching can break control flow if applied blindly.
- Always validate patched binaries in isolated test environments.
- If build fails with file-lock errors, stop running MCP process first.

---

## 11) Extension guidance

Recommended additions:
- patch dry-run mode (show modifications without writing)
- patch plan tool (`patch_in_order_to`) returning deterministic steps
- call graph helpers for upstream condition tracing
- safe patch templates for common scenarios

When adding tools:
1) add method in `DnSpyTools.cs`
2) implement logic in service
3) ensure token-rich output
4) update README + this AGENT.md
