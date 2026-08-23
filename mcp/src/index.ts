import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { spawn } from "child_process";
import { z } from "zod";

const BASE_URL = process.env.DIZ_API_URL ?? "http://localhost:5743";

async function api(
  method: "GET" | "POST" | "PUT" | "PATCH" | "DELETE",
  path: string,
  body?: unknown
): Promise<unknown> {
  const url = `${BASE_URL}${path}`;
  const init: RequestInit = { method, headers: { "Content-Type": "application/json" } };
  if (body !== undefined) init.body = JSON.stringify(body);

  let res: Response;
  try {
    res = await fetch(url, init);
  } catch {
    throw new Error(
      `Cannot reach DiztinGUIsh at ${BASE_URL}. ` +
      "Ensure the application is running, or use start_diz to launch it."
    );
  }

  const text = await res.text();
  const json = text.length > 0 ? JSON.parse(text) : { ok: true };
  if (!res.ok) {
    const msg = (json as any).message ?? (json as any).error ?? res.statusText;
    throw new Error(`Diz API error ${res.status}: ${msg}`);
  }
  return json;
}

function text(result: unknown) {
  return { content: [{ type: "text" as const, text: JSON.stringify(result, null, 2) }] };
}

// Every tool that names a location to annotate takes a SNES address. PC (file) offsets
// remain only where the coordinate genuinely is a file position (GetByte, GetBytes) and
// as an output field. Never convert between the two by hand: the mapping depends on the
// project's map mode, so a memorised mask silently corrupts data on the other project.
const SNES_ADDRESS = z.string().describe(
  "SNES address as a hex string, e.g. 'C14CE5'. No 0x prefix required.");

// Mirror canonicalisation depends on the project's map mode and ROM speed, so no fixed
// range can be stated here without rotting. GetProjectInfo reports the live range.
const MIRROR_NOTE =
  "Mirror addresses are canonicalised to the project's canonical bank range before " +
  "storage; the response echoes the canonical address actually written. " +
  "Call GetProjectInfo for that range rather than assuming one. ";

const DIRECTIVE_NOTE =
  "Comment text may carry export-substitution directives (!!o / !!db / !!dw / " +
  "!!dl / !!n) — see notes/diz_directives.md. ";

async function isDizRunning(): Promise<boolean> {
  try {
    const res = await fetch(`${BASE_URL}/project`, { signal: AbortSignal.timeout(1000) });
    return res.status !== 0;
  } catch {
    return false;
  }
}

const server = new McpServer({
  name: "diz-mcp",
  version: "1.0.0",
});

// ── Launcher ─────────────────────────────────────────────────────────────────

server.tool(
  "start_diz",
  "Launch the DiztinGUIsh application if it is not already running. " +
  "Waits up to 15 seconds for the server to become reachable. " +
  "Requires the DIZ_PATH environment variable to be set to the path of " +
  "the DiztinGUIsh executable.",
  {},
  async () => {
    if (await isDizRunning())
      return { content: [{ type: "text", text: "DiztinGUIsh is already running." }] };

    const dizPath = process.env.DIZ_PATH;
    if (!dizPath)
      throw new Error(
        "DIZ_PATH environment variable is not set. " +
        "Set it to the path of the DiztinGUIsh executable."
      );

    spawn(dizPath, [], { detached: true, stdio: "ignore" }).unref();

    for (let i = 0; i < 30; i++) {
      await new Promise(r => setTimeout(r, 500));
      if (await isDizRunning())
        return { content: [{ type: "text", text: "DiztinGUIsh launched and server is reachable." }] };
    }
    throw new Error(
      "DiztinGUIsh was launched but the server did not become reachable " +
      "within 15 seconds. Check that the application started correctly."
    );
  }
);

// ── Project / selection ───────────────────────────────────────────────────────

server.tool(
  "GetProjectInfo",
  "Get metadata for the currently loaded project: ROM name, map mode, " +
  "size, bank count, checksum validity, and unsaved-changes flag. " +
  "Also returns canonicalBankLow/canonicalBankHigh — the SNES bank range this project " +
  "canonicalises mirror addresses into. That range is derived from the project's map " +
  "mode and ROM speed and differs between projects, so read it here instead of " +
  "assuming a fixed range or a conversion formula.",
  {},
  async () => text(await api("GET", "/project"))
);

server.tool(
  "GetSelection",
  "Get the PC offset and SNES address of the currently selected byte in the UI.",
  {},
  async () => text(await api("GET", "/selection"))
);

server.tool(
  "SetSelection",
  "Move the UI cursor to a PC offset.",
  { pcOffset: z.number().int().describe("PC (file) offset to select") },
  async ({ pcOffset }) => {
    await api("PUT", "/selection", { PcOffset: pcOffset });
    return text({ ok: true });
  }
);

// ── Byte reads ────────────────────────────────────────────────────────────────

server.tool(
  "GetByte",
  "Get all metadata for the ROM byte at a PC offset: flag type, " +
  "instruction string, CPU flags, SNES address, and raw value.",
  { pcOffset: z.number().int().describe("PC (file) offset") },
  async ({ pcOffset }) => text(await api("GET", `/byte/${pcOffset}`))
);

server.tool(
  "GetByteBySnesAddress",
  "Get all metadata for the ROM byte at a SNES address. " +
  "Use this when working from an emulator or disassembler that surfaces " +
  "SNES addresses directly. Returns an error for non-ROM addresses " +
  "(WRAM, hardware registers, unmapped). " +
  "Returns byte metadata only — never label info; call GetLabel to check " +
  "for or confirm a label.",
  { snesAddress: z.string().describe("SNES address as a hex string, e.g. 'C14CE5'. No 0x prefix required.") },
  async ({ snesAddress }) => text(await api("GET", `/byte/snes/${snesAddress}`))
);

server.tool(
  "GetBytes",
  "Get metadata for up to 1024 consecutive bytes starting at a PC offset. " +
  "Prefer this over repeated GetByte calls for bulk reads.",
  {
    from: z.number().int().describe("Starting PC offset"),
    count: z.number().int().min(1).max(1024).describe("Number of bytes (max 1024)"),
  },
  async ({ from, count }) => text(await api("GET", `/bytes?from=${from}&count=${count}`))
);

// ── Classification ────────────────────────────────────────────────────────────

server.tool(
  "SetByteFlag",
  "Set the flag type for the ROM byte at a SNES address. " +
  "Valid values: Unreached, Opcode, Operand, Data8Bit, Data16Bit, Data24Bit, " +
  "Data32Bit, Pointer16Bit, Pointer24Bit, Pointer32Bit, Text, Graphics, Music, Empty. " +
  "Returns the annotated byte's full metadata, so no follow-up read is needed. " +
  "Errors for non-ROM addresses (WRAM, hardware registers) — a label is the right " +
  "annotation there; use SetLabelAtAddress.",
  {
    snesAddress: SNES_ADDRESS,
    flagType: z.string().describe("Flag type name"),
  },
  async ({ snesAddress, flagType }) =>
    text(await api("PUT", `/flag/${snesAddress}`, { FlagType: flagType }))
);

server.tool(
  "MarkRange",
  "Mark a contiguous range of bytes with a flag type, starting at a SNES address. " +
  "Use this for bulk classification of data regions. " +
  "The range is counted forward in ROM file order, so 'count' — not an end address — " +
  "defines it unambiguously regardless of the project's map mode. " +
  "Returns the canonical start address and the first byte's metadata.",
  {
    snesStart: SNES_ADDRESS.describe(
      "SNES address of the first byte to mark, as a hex string, e.g. 'C14CE5'. No 0x prefix required."),
    flagType: z.string().describe("Flag type name"),
    count: z.number().int().min(1).describe("Number of bytes to mark"),
  },
  async ({ snesStart, flagType, count }) =>
    text(await api("POST", "/mark", { SnesStart: snesStart, FlagType: flagType, Count: count }))
);

// ── Annotations ───────────────────────────────────────────────────────────────

server.tool(
  "SetByteComment",
  "Set or overwrite the standalone byte comment at a SNES address. " +
  "Pass an empty string to clear it. " +
  "A byte comment cannot coexist with a label at the same address — if a label is " +
  "present this fails with a conflict error; put the comment on the label instead " +
  "(PatchLabel). Errors for non-ROM addresses (WRAM, hardware registers). " +
  "Returns the canonical address written plus the annotated byte's full metadata, " +
  "so no follow-up read is needed. " +
  MIRROR_NOTE +
  DIRECTIVE_NOTE,
  {
    snesAddress: SNES_ADDRESS,
    text: z.string().describe("Comment text. Empty string clears the comment."),
  },
  async ({ snesAddress, text: commentText }) =>
    text(await api("PUT", `/comments/${snesAddress}`, { Text: commentText }))
);

server.tool(
  "GetAllLabels",
  "Get all labels defined in the project.",
  {},
  async () => text(await api("GET", "/labels"))
);

server.tool(
  "GetLabel",
  "Get the label at a SNES address. Returns an error if no label exists there. " +
  MIRROR_NOTE,
  { snesAddress: z.string().describe("SNES address as a hex string, e.g. 'C14CE5'. No 0x prefix required.") },
  async ({ snesAddress }) => text(await api("GET", `/labels/${snesAddress}`))
);

server.tool(
  "SetLabelAtAddress",
  "Set or overwrite a label at a SNES address. This is the only label-write tool. " +
  "Unlike the byte-level tools, non-ROM addresses are accepted: a label on WRAM or a " +
  "hardware register is a legitimate equate, and the response's 'byte' field is null there. " +
  "A label cannot be created where a standalone byte comment exists — delete " +
  "the byte comment first (DeleteComment), then create the label with its comment. " +
  "Fails with a conflict error otherwise. " +
  "Returns the canonical address stored plus the underlying byte's metadata. " +
  MIRROR_NOTE +
  DIRECTIVE_NOTE,
  {
    snesAddress: SNES_ADDRESS,
    name: z.string().describe("Label name"),
    comment: z.string().describe("Label comment"),
  },
  async ({ snesAddress, name, comment }) =>
    text(await api("PUT", `/labels/${snesAddress}`, { Name: name, Comment: comment }))
);

server.tool(
  "SaveProject",
  "Save the project to disk. Call this after making annotation changes " +
  "to persist them. Returns the file path the project was saved to.",
  {},
  async () => {
    const result = await api("POST", "/project/save") as { path: string };
    return text(`Saved to: ${result.path}`);
  }
);

server.tool(
  "OpenProject",
  "Open a .dizraw project file in DiztinGUIsh, replacing any currently " +
  "loaded project. Provide an absolute filesystem path to the .dizraw file.",
  {
    path: z.string()
      .describe("Absolute path to the .dizraw project file."),
  },
  async ({ path }) => ({
    content: [{
      type: "text",
      text: JSON.stringify(
        await api("POST", "/project/open", { path }),
        null, 2
      )
    }]
  })
);

server.tool(
  "PatchLabel",
  "Update the name, comment, or both fields of a label at a SNES address. " +
  "Omit either field to leave it unchanged. Use this instead of " +
  "SetLabelAtAddress when you only want to update the comment without " +
  "knowing the current name, or vice versa. " +
  MIRROR_NOTE +
  "If no label exists yet at the address, a label cannot be created where a " +
  "standalone byte comment exists — delete the byte comment first, then create " +
  "the label with its comment. Fails with a conflict error otherwise. " +
  "Comment text may carry export-substitution directives (!!o / !!db / !!dw / " +
  "!!dl / !!n) — see notes/diz_directives.md.",
  {
    snesAddress: z.string().describe("SNES address as a hex string, e.g. 'C14CE5'. No 0x prefix required."),
    name: z.string().optional().describe("New label name. Omit to keep the existing name."),
    comment: z.string().optional().describe("New label comment. Omit to keep the existing comment."),
  },
  async ({ snesAddress, name, comment }) => {
    const body: Record<string, string> = {};
    if (name !== undefined) body.name = name;
    if (comment !== undefined) body.comment = comment;
    return text(await api("PATCH", `/labels/${snesAddress}`, body));
  }
);

server.tool(
  "SetLabelContextMapping",
  "Add or update a context-specific name override on a label. " +
  "The label must already exist. The context string should match the " +
  "ContextToApply value of a Region — within any region tagged with that " +
  "context, the label will appear under nameOverride instead of its primary " +
  "name in the disassembly output. Useful for labels that serve different " +
  "roles in different game states.",
  {
    snesAddress: z.string()
      .describe("SNES address of the label, e.g. '414CE5'"),
    context: z.string()
      .describe("Context name matching a Region's ContextToApply, e.g. 'combat'"),
    nameOverride: z.string()
      .describe("Name to use for this label within matching regions"),
  },
  async ({ snesAddress, context, nameOverride }) =>
    text(await api("PUT", `/labels/${snesAddress}/contexts/${context}`, { nameOverride }))
);

server.tool(
  "RemoveLabelContextMapping",
  "Remove a context-specific name override from a label. " +
  "Returns an error if no mapping with that context name exists.",
  {
    snesAddress: z.string()
      .describe("SNES address of the label, e.g. '414CE5'"),
    context: z.string()
      .describe("Context name of the mapping to remove"),
  },
  async ({ snesAddress, context }) =>
    text(await api("DELETE", `/labels/${snesAddress}/contexts/${context}`))
);

server.tool(
  "DeleteLabel",
  "Delete the label at a SNES address. Returns the deleted label's name, " +
  "comment, and context mappings so you can confirm what was removed. " +
  "Returns an error if no label exists at that address. " +
  "Use this to correct a label placed at the wrong address — delete it " +
  "here, then call SetLabelAtAddress on the correct address.",
  {
    snesAddress: z.string()
      .describe("SNES address of the label to delete, e.g. '47A6A8'"),
  },
  async ({ snesAddress }) => ({
    content: [{
      type: "text" as const,
      text: JSON.stringify(
        await api("DELETE", `/labels/${snesAddress}`),
        null, 2)
    }]
  })
);

server.tool(
  "GetAllComments",
  "Get all comments defined in the project.",
  {},
  async () => text(await api("GET", "/comments"))
);

server.tool(
  "GetComment",
  "Get the comment at a SNES address. Returns an error if none exists. " +
  MIRROR_NOTE,
  { snesAddress: z.string().describe("SNES address as a hex string, e.g. 'C14CE5'. No 0x prefix required.") },
  async ({ snesAddress }) => text(await api("GET", `/comments/${snesAddress}`))
);

server.tool(
  "DeleteComment",
  "Delete the comment at a SNES address. Returns the deleted comment's text " +
  "so you can confirm what was removed. Returns an error if no comment exists " +
  "at that address. " +
  MIRROR_NOTE,
  { snesAddress: z.string().describe("SNES address of the comment to delete, as a hex string, e.g. 'C14CE5'. No 0x prefix required.") },
  async ({ snesAddress }) => text(await api("DELETE", `/comments/${snesAddress}`))
);

// ── Navigation ────────────────────────────────────────────────────────────────

server.tool(
  "GetIntermediateAddress",
  "Resolve the target address the byte at a SNES address points to, returned as a " +
  "SNES address. Works for branch/jump instructions and pointer data bytes " +
  "(Pointer16Bit, Pointer24Bit, Pointer32Bit). " +
  "Returns an error if the byte has no meaningful target, or if the address names no ROM byte.",
  { snesAddress: SNES_ADDRESS },
  async ({ snesAddress }) => text(await api("GET", `/ia/${snesAddress}`))
);

server.tool(
  "AutoStep",
  "Run the safe auto-step algorithm from a SNES address, automatically marking " +
  "opcodes and operands until it can no longer continue safely. " +
  "Returns the start and stop positions as both SNES addresses and PC offsets.",
  { snesAddress: SNES_ADDRESS.describe(
      "SNES address to start auto-stepping from, as a hex string, e.g. 'C14CE5'. No 0x prefix required.") },
  async ({ snesAddress }) => text(await api("POST", `/autostep/${snesAddress}`))
);

server.tool(
  "FindNextUnreached",
  "Find the next unreached branch point forward or backward from a PC offset. " +
  "Returns FoundAt = -1 if none is found. Use this to locate unanalysed code " +
  "that is reachable from already-classified instructions.",
  {
    from: z.number().int().describe("Starting PC offset"),
    searchForward: z.boolean().describe("true to search forward, false to search backward"),
  },
  async ({ from, searchForward }) =>
    text(await api("GET", `/find-next-unreached?from=${from}&searchForward=${searchForward}`))
);

server.tool(
  "DetectNextPointerTable",
  "Scan forward or backward from a PC offset for the next unlabelled pointer table, " +
  "detected by addressing-mode usage patterns. Returns FoundAt = -1 if none is found. " +
  "Use this to discover pointer tables that have not yet been annotated.",
  {
    from: z.number().int().describe("Starting PC offset"),
    searchForward: z.boolean().default(true).describe("true to search forward, false to search backward"),
  },
  async ({ from, searchForward }) =>
    text(await api("GET", `/detect-next-pointer-table?from=${from}&searchForward=${searchForward}`))
);

// ── Regions ───────────────────────────────────────────────────────────────────

server.tool(
  "GetAllRegions",
  "List all named address regions in the project.",
  {},
  async () => text(await api("GET", "/regions"))
);

server.tool(
  "GetRegionAt",
  "Get the highest-priority region containing a SNES address.",
  { snesAddress: z.string().describe("SNES address as a hex string, e.g. 'C14CE5'. No 0x prefix required.") },
  async ({ snesAddress }) => text(await api("GET", `/regions/at/${snesAddress}`))
);

server.tool(
  "CreateRegion",
  "Create a new named region covering a SNES address range. Set exportType to 'Binary' or 'Asset' (with assetType/assetName/etc.) to mark the range for binary/asset export — the region-based replacement for the retired !!incbin directive.",
  {
    startSnesAddress: z.string().describe("Start SNES address as a hex string, e.g. 'C00000'. No 0x prefix required."),
    endSnesAddress: z.string().describe("End SNES address as a hex string (inclusive), e.g. 'C0FFFF'. No 0x prefix required."),
    regionName: z.string().describe("Name for the region"),
    contextToApply: z.string().describe("Context string to apply to the region"),
    priority: z.number().int().describe("Region priority (higher wins when overlapping)"),
    exportSeparateFile: z.boolean().describe("Whether to export this region to a separate .asm file"),
    exportType: z.string().optional().describe("Region export type: 'Assembly' (default), 'Binary', or 'Asset'."),
    assetType: z.string().optional().describe("Asset type/codec name (when exportType is 'Asset')."),
    assetVersion: z.string().optional().describe("Asset codec version (when exportType is 'Asset')."),
    assetName: z.string().optional().describe("Asset/output name for the binary or asset region."),
    assetOptions: z.string().optional().describe("Extra codec options for the asset region."),
  },
  async ({ startSnesAddress, endSnesAddress, regionName, contextToApply, priority, exportSeparateFile, exportType, assetType, assetVersion, assetName, assetOptions }) =>
    text(await api("POST", "/regions", {
      StartSnesAddress: startSnesAddress,
      EndSnesAddress: endSnesAddress,
      RegionName: regionName,
      ContextToApply: contextToApply,
      Priority: priority,
      ExportSeparateFile: exportSeparateFile,
      ExportType: exportType,
      AssetType: assetType,
      AssetVersion: assetVersion,
      AssetName: assetName,
      AssetOptions: assetOptions,
    }))
);

server.tool(
  "DeleteRegion",
  "Delete the region at a given index. Use GetAllRegions to find indices.",
  { index: z.number().int().min(0).describe("Region index to delete") },
  async ({ index }) => {
    await api("DELETE", `/regions/${index}`);
    return text({ ok: true });
  }
);

// ── Diagnostics ───────────────────────────────────────────────────────────────

server.tool(
  "RescanInOutPoints",
  "Recompute all InPoint and OutPoint flags across the ROM.",
  {},
  async () => {
    await api("POST", "/diagnostics/rescan-inout-points");
    return text({ ok: true });
  }
);

server.tool(
  "FixMisalignedFlags",
  "Fix bytes incorrectly classified due to instruction boundary misalignment. " +
  "Returns the number of bytes corrected.",
  {},
  async () => text(await api("POST", "/diagnostics/fix-misaligned-flags"))
);

server.tool(
  "GetMisalignmentReport",
  "Report instruction boundary misalignments without modifying any data.",
  {},
  async () => text(await api("GET", "/diagnostics/misalignment-report"))
);

server.tool(
  "NormalizeWramLabels",
  "Normalize WRAM mirror label addresses to their canonical form.",
  {},
  async () => {
    await api("POST", "/diagnostics/normalize-wram-labels");
    return text({ ok: true });
  }
);

server.tool(
  "FixChecksum",
  "Recalculate and write a valid SNES ROM checksum. Save the project afterward.",
  {},
  async () => {
    await api("POST", "/diagnostics/fix-checksum");
    return text({ ok: true });
  }
);

// ─────────────────────────────────────────────────────────────────────────────

const transport = new StdioServerTransport();
await server.connect(transport);

// ── Harness-compat shim ──────────────────────────────────────────────────────
// Some Claude Code builds double-JSON-encode SCALAR STRING tool arguments before
// sending them (e.g. snesAddress "C1D52A" arrives as the 8-char value "\"C1D52A\""),
// which then fails validation. This reverses exactly ONE extra JSON-string layer on
// any string leaf at any depth, and ONLY when the value is unambiguously a JSON-
// encoded string (starts and ends with a double-quote and parses to a string). It is
// a no-op on clean values, so it stays correct after the harness bug is fixed.
// Arrays/numbers/objects are recursed into but never reinterpreted.
function unmangleToolArg(x: any): any {
  if (typeof x === "string") {
    if (x.length >= 2 && x.charCodeAt(0) === 0x22 && x.charCodeAt(x.length - 1) === 0x22) {
      try {
        const parsed = JSON.parse(x);
        if (typeof parsed === "string") return parsed; // strip one encoding layer only
      } catch { /* genuine string that merely starts/ends with a quote: leave it */ }
    }
    return x;
  }
  if (Array.isArray(x)) return x.map(unmangleToolArg);
  if (x && typeof x === "object") {
    const out: Record<string, any> = {};
    for (const k of Object.keys(x)) out[k] = unmangleToolArg(x[k]);
    return out;
  }
  return x;
}
{
  const t = transport as any;
  const deliver = t.onmessage;
  if (typeof deliver === "function") {
    t.onmessage = (message: any, ...rest: any[]) => {
      try {
        if (message && message.method === "tools/call" && message.params && message.params.arguments) {
          message.params.arguments = unmangleToolArg(message.params.arguments);
        }
      } catch { /* never let the shim break message delivery */ }
      return deliver.call(t, message, ...rest);
    };
  }
}
