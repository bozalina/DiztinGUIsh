using Microsoft.AspNetCore.Diagnostics;
using Scalar.AspNetCore;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Diz.App.Api.Test")]

namespace Diz.App.Api;

public class DizApiServer : IAsyncDisposable
{
    private readonly DizApiService _service;
    private WebApplication? _app;

    public DizApiServer(DizApiService apiService)
    {
        _service = apiService;
    }

    public void Start()
    {
        _app = BuildApp();
        _ = _app.StartAsync();
        Console.WriteLine(
            "[Diz.Api] Listening on http://localhost:5743\n" +
            "          API docs : http://localhost:5743/scalar/v1");
    }

    public async Task Stop()
    {
        if (_app != null)
            await _app.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
            await _app.DisposeAsync();
    }

    private WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://localhost:5743");
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(_service);

        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Info.Title       = "DiztinGUIsh API";
                doc.Info.Version     = "v1";
                doc.Info.Description =
                    "Embedded HTTP API for DiztinGUIsh. Exposes the loaded " +
                    "disassembly project for external tooling such as MCP clients " +
                    "and debug consumers.\n\n" +
                    "Addressing: every endpoint that names a location to annotate takes a " +
                    "SNES address as a hex string (e.g. \"C14CE5\"). PC (file) offsets remain " +
                    "only where the coordinate genuinely is a file position — GET /byte/{pcOffset}, " +
                    "GET /bytes, and the selection cursor — and as an output field alongside the " +
                    "canonical SNES address. Never convert between the two by hand: the mapping " +
                    "depends on the project's map mode. Ask GET /project for the canonical bank range.";
                return Task.CompletedTask;
            });
        });

        var app = builder.Build();

        app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
        {
            var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
            if (ex is DizApiException dae)
            {
                ctx.Response.StatusCode = dae.Kind switch
                {
                    DizApiErrorKind.NoProjectLoaded => 503,
                    DizApiErrorKind.NotFound        => 404,
                    DizApiErrorKind.InvalidArgument => 400,
                    DizApiErrorKind.Conflict        => 409,
                    _                               => 500,
                };
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsJsonAsync(new { error = dae.Message });
            }
        }));

        app.MapOpenApi();            // spec at /openapi/v1.json
        app.MapScalarApiReference(); // UI at /scalar/v1

        MapRoutes(app);
        return app;
    }

    internal void MapRoutes(WebApplication app)
    {
        var api = app.MapGroup("/");

        // ── Project ──────────────────────────────────────────────────────────

        api.MapGet("/project", () => _service.GetProjectInfo())
           .WithTags("Project")
           .WithName("GetProject")
           .WithSummary("Get loaded project metadata")
           .WithDescription(
               "Returns ROM metadata for the currently loaded project: game name, map mode, " +
               "ROM size, bank count, checksum validity, and unsaved-changes flag. " +
               "Also returns canonicalBankLow/canonicalBankHigh — the SNES bank range this " +
               "project's mirror addresses are canonicalised into. That range is derived from " +
               "the project's map mode and ROM speed and differs between projects, so read it " +
               "here rather than assuming a fixed range. " +
               "Returns 503 if no project is loaded.");

        api.MapPost("/project/save", async () =>
            {
                var filename = await _service.SaveProject();
                return Results.Ok(new { saved = true, path = filename });
            })
           .WithTags("Project")
           .WithName("SaveProject")
           .WithSummary("Save the project to disk")
           .WithDescription(
               "Saves the current project to its existing file path. Returns 400 if " +
               "the project has never been saved (no path set) or if the save fails. " +
               "Returns 503 if no project is loaded.");

        api.MapPost("/project/open", async (HttpContext ctx) =>
            {
                var body = await ctx.Request.ReadFromJsonAsync<OpenProjectBody>();
                if (body == null) return Results.BadRequest("Invalid body");
                await _service.OpenProject(body.Path);
                return Results.Ok(new { opened = true, path = body.Path });
            })
           .WithTags("Project")
           .WithName("OpenProject")
           .WithSummary("Open a project file")
           .WithDescription(
               "Opens a .dizraw project file, replacing any currently loaded project. " +
               "If the current project has unsaved changes the application may prompt " +
               "the user before proceeding. " +
               "Returns 404 if the file does not exist or cannot be parsed. " +
               "Returns 400 if no path is supplied.");

        // ── Selection ────────────────────────────────────────────────────────

        api.MapGet("/selection", () => _service.GetSelection())
           .WithTags("Selection")
           .WithName("GetSelection")
           .WithSummary("Get the current cursor selection")
           .WithDescription(
               "Returns the currently selected PC offset and its equivalent SNES address. " +
               "The SNES address is -1 if no project is loaded.");

        api.MapPut("/selection", async (SelectionBody body) =>
           {
               await _service.SetSelection(body.PcOffset);
               return Results.Ok(await _service.GetSelection());
           })
           .WithTags("Selection")
           .WithName("SetSelection")
           .WithSummary("Move the cursor to a PC offset")
           .WithDescription(
               "Sets the active cursor position to the given PC (file) offset. " +
               "The UI will scroll to the selected byte.");

        // ── Bytes ────────────────────────────────────────────────────────────

        api.MapGet("/byte/{pcOffset:int}", (int pcOffset) => _service.GetByte(pcOffset))
           .WithTags("Bytes")
           .WithName("GetByte")
           .WithSummary("Get all metadata for a single byte")
           .WithDescription(
               "Returns flag type, in/out point, data bank, direct page, M/X flags, " +
               "architecture, disassembled instruction string, instruction length, " +
               "SNES address, and raw byte value for the given PC offset. " +
               "Returns 503 if no project is loaded.");

        api.MapGet("/byte/snes/{snesAddress}", (string snesAddress) =>
            _service.GetByteBySnesAddress(snesAddress))
           .WithTags("Bytes")
           .WithName("GetByteBySnesAddress")
           .WithSummary("Get all metadata for the ROM byte at a SNES address")
           .WithDescription(
               "Converts the SNES address to a PC offset and returns the same metadata " +
               "as GET /byte/{pcOffset}. Use this when the address comes from an emulator " +
               "or disassembler rather than from a PC offset. Returns 404 for addresses " +
               "that do not map to ROM — including WRAM ($7Exxxx/$7Fxxxx), hardware " +
               "registers ($21xx, $42xx, $43xx), and any unmapped regions.");

        api.MapGet("/bytes", (int from, int count) => _service.GetBytes(from, count))
           .WithTags("Bytes")
           .WithName("GetBytes")
           .WithSummary("Get metadata for a contiguous byte range")
           .WithDescription(
               "Returns per-byte metadata for up to 1024 bytes starting at the given PC offset. " +
               "The count is clamped to min(count, 1024) and the range is clamped to [from, romSize). " +
               "Returns 503 if no project is loaded.");

        // ── Classification ───────────────────────────────────────────────────

        api.MapPut("/flag/{snesAddress}", async (string snesAddress, FlagBody body) =>
               Results.Ok(await _service.SetByteFlag(snesAddress, body.FlagType)))
           .WithTags("Classification")
           .WithName("SetByteFlag")
           .WithSummary("Set the flag type for the byte at a SNES address")
           .WithDescription(
               "Marks the ROM byte at the given SNES address with the specified FlagType " +
               "(e.g. Opcode, Operand, Data8Bit, Unreached). " +
               "Returns the full metadata of the annotated byte, including its canonical " +
               "SNES address and PC offset. " +
               "Returns 400 for an unknown flag value, 404 if the address names no ROM byte " +
               "(WRAM, hardware register, unmapped), 503 if no project is loaded.");

        api.MapPost("/mark", async (MarkBody body) =>
               Results.Ok(await _service.MarkRange(body.SnesStart, body.FlagType, body.Count)))
           .WithTags("Classification")
           .WithName("MarkRange")
           .WithSummary("Mark a contiguous byte range with a flag type")
           .WithDescription(
               "Applies the specified FlagType to 'count' bytes starting at the given SNES address. " +
               "The range is counted forward in PC (file) order, so 'count' — not an end address — " +
               "defines it unambiguously across map modes. " +
               "Returns 400 for an unknown flag value or a non-positive count, 404 if the start " +
               "address names no ROM byte, 503 if no project is loaded.");

        // ── Annotations ──────────────────────────────────────────────────────

        api.MapGet("/labels", () => _service.GetAllLabels())
           .WithTags("Annotations")
           .WithName("GetAllLabels")
           .WithSummary("List all labels in the project")
           .WithDescription(
               "Returns every label defined in the loaded project as a flat list of " +
               "{snesAddress, name, comment} objects. " +
               "Returns 503 if no project is loaded.");

        api.MapGet("/labels/{snesAddress}", (string snesAddress) => _service.GetLabel(snesAddress))
           .WithTags("Annotations")
           .WithName("GetLabel")
           .WithSummary("Get the label at a SNES address")
           .WithDescription(
               "Returns the name and comment for the label at the given SNES address. " +
               "Returns 404 if no label exists at that address, 503 if no project is loaded. " +
               "Mirror addresses are canonicalised to the project's canonical range before storage (see GetProjectInfo).");

        api.MapPut("/labels/{snesAddress}", async (string snesAddress, LabelPutBody body) =>
               Results.Ok(await _service.SetLabelAtAddress(snesAddress, body.Name, body.Comment)))
           .WithTags("Annotations")
           .WithName("SetLabel")
           .WithSummary("Create or overwrite a label at a SNES address")
           .WithDescription(
               "Creates or overwrites the label (name + comment) at the given SNES address. " +
               "This is the only label-write verb; labels are always addressed by SNES address. " +
               "Non-ROM addresses (WRAM, hardware registers) are accepted — a label there is a " +
               "legitimate equate, and the response's 'byte' field is null for them. " +
               "Returns the canonical address the label was stored at. " +
               "Returns 409 if a standalone byte comment already exists there, 503 if no project is loaded. " +
               "Mirror addresses are canonicalised to the project's canonical range before storage (see GetProjectInfo).");

        api.MapPatch("/labels/{snesAddress}", async (string snesAddress, HttpContext ctx) =>
            {
                var body = await ctx.Request.ReadFromJsonAsync<PatchLabelBody>();
                if (body == null) return Results.BadRequest("Invalid body");
                return Results.Ok(await _service.PatchLabel(snesAddress, body.Name, body.Comment));
            })
           .WithTags("Annotations")
           .WithName("PatchLabel")
           .WithSummary("Partially update a label")
           .WithDescription(
               "Updates one or both fields of a label at a SNES address. Omit a field " +
               "to leave it unchanged. If no label exists at the address a new one is " +
               "created. Returns 400 if neither field is supplied. " +
               "Mirror addresses are canonicalised to the project's canonical range before storage (see GetProjectInfo).");

        api.MapPut("/labels/{snesAddress}/contexts/{context}",
            async (string snesAddress, string context, HttpContext ctx) =>
            {
                var body = await ctx.Request
                    .ReadFromJsonAsync<SetContextMappingBody>();
                if (body == null) return Results.BadRequest("Invalid body");
                return Results.Ok(await _service.SetLabelContextMapping(
                    snesAddress, context, body.NameOverride));
            })
           .WithTags("Annotations")
           .WithName("SetLabelContextMapping")
           .WithSummary("Add or update a context mapping on a label")
           .WithDescription(
               "Adds or updates a (Context, NameOverride) entry on the label at the " +
               "given SNES address. The label must already exist. The context name " +
               "should match the ContextToApply value of a Region for the override " +
               "to take effect in the disassembly output. " +
               "Mirror addresses are canonicalised to the project's canonical range before storage (see GetProjectInfo).");

        api.MapDelete("/labels/{snesAddress}/contexts/{context}",
            async (string snesAddress, string context) =>
                Results.Ok(await _service.RemoveLabelContextMapping(
                    snesAddress, context)))
           .WithTags("Annotations")
           .WithName("RemoveLabelContextMapping")
           .WithSummary("Remove a context mapping from a label")
           .WithDescription(
               "Removes the context mapping with the given context name from the " +
               "label at the given SNES address. Returns 404 if no such mapping exists.");

        api.MapDelete("/labels/{snesAddress}",
            async (string snesAddress) =>
                Results.Ok(await _service.DeleteLabel(snesAddress)))
           .WithTags("Annotations")
           .WithName("DeleteLabel")
           .WithSummary("Delete a label")
           .WithDescription(
               "Removes the label at the given SNES address and returns the deleted " +
               "label's data. Returns 404 if no label exists at that address. " +
               "Mirror addresses are canonicalised to the project's canonical range before storage (see GetProjectInfo).");

        api.MapGet("/comments", () => _service.GetAllComments())
           .WithTags("Annotations")
           .WithName("GetAllComments")
           .WithSummary("List all comments in the project")
           .WithDescription(
               "Returns every comment defined in the loaded project as a flat list of " +
               "{snesAddress, text} objects. " +
               "Returns 503 if no project is loaded.");

        api.MapGet("/comments/{snesAddress}", (string snesAddress) => _service.GetComment(snesAddress))
           .WithTags("Annotations")
           .WithName("GetComment")
           .WithSummary("Get the comment at a SNES address")
           .WithDescription(
               "Returns the comment text at the given SNES address. " +
               "Returns 404 if no comment exists at that address, 503 if no project is loaded. " +
               "Mirror addresses are canonicalised to the project's canonical range before storage (see GetProjectInfo).");

        api.MapPut("/comments/{snesAddress}", async (string snesAddress, SetCommentBody body) =>
               Results.Ok(await _service.SetByteComment(snesAddress, body.Text)))
           .WithTags("Annotations")
           .WithName("SetByteComment")
           .WithSummary("Set the standalone byte comment at a SNES address")
           .WithDescription(
               "Creates or overwrites the standalone byte comment at the given SNES address. " +
               "Pass an empty string to clear it. Returns the canonical address written to plus " +
               "the full metadata of the annotated byte. " +
               "A byte comment may not coexist with a label at the same address: returns 409 if a " +
               "label is present — put the comment on the label instead (PatchLabel). " +
               "Returns 404 if the address names no ROM byte, 503 if no project is loaded. " +
               "Mirror addresses are canonicalised to the project's canonical range before storage (see GetProjectInfo).");

        api.MapDelete("/comments/{snesAddress}",
            async (string snesAddress) =>
                Results.Ok(await _service.DeleteComment(snesAddress)))
           .WithTags("Annotations")
           .WithName("DeleteComment")
           .WithSummary("Delete a comment")
           .WithDescription(
               "Removes the comment at the given SNES address and returns the deleted " +
               "comment's data. Returns 404 if no comment exists at that address, 503 if no project is loaded. " +
               "Mirror addresses are canonicalised to the project's canonical range before storage (see GetProjectInfo).");

        // ── Navigation ───────────────────────────────────────────────────────

        api.MapPost("/autostep/{snesAddress}", (string snesAddress) => _service.AutoStep(snesAddress))
           .WithTags("Navigation")
           .WithName("AutoStep")
           .WithSummary("Auto-step (safe) from a SNES address")
           .WithDescription(
               "Runs the safe auto-step algorithm starting at the given SNES address, automatically " +
               "marking opcodes and operands until it can no longer safely continue. " +
               "Returns the start and stop positions as both SNES addresses and PC offsets, plus " +
               "the flag type at the stop position. " +
               "Returns 404 if the address names no ROM byte, 503 if no project is loaded.");

        api.MapGet("/ia/{snesAddress}", (string snesAddress) => _service.GetIntermediateAddress(snesAddress))
           .WithTags("Navigation")
           .WithName("GetIntermediateAddress")
           .WithSummary("Resolve the navigation target address of a byte")
           .WithDescription(
               "Returns the target address for the byte at the given SNES address, as a SNES address. " +
               "Works for both instruction bytes (branch/jump/call targets) and pointer data bytes " +
               "(Pointer16Bit reads a 16-bit pointer and reconstructs the full SNES address using the stored data bank; " +
               "Pointer24Bit and Pointer32Bit read the pointer directly). " +
               "Returns 404 if the byte has no meaningful target address or the address names no ROM byte, " +
               "503 if no project is loaded.");

        api.MapGet("/find-next-unreached", (int from, bool searchForward) =>
               _service.FindNextUnreached(from, searchForward))
           .WithTags("Navigation")
           .WithName("FindNextUnreached")
           .WithSummary("Find the next unreached branch point")
           .WithDescription(
               "Scans forward or backward from the given PC offset to find the next byte that is " +
               "Unreached and sits at a reachable in-point. " +
               "Returns FoundAt = -1 if none is found. " +
               "Returns 503 if no project is loaded.");

        api.MapGet("/detect-next-pointer-table", (int from, bool searchForward) =>
               _service.DetectNextPointerTable(from, searchForward))
           .WithTags("Navigation")
           .WithName("DetectNextPointerTable")
           .WithSummary("Detect the next unlabelled pointer table")
           .WithDescription(
               "Scans forward or backward from the given PC offset for the next unlabelled pointer " +
               "table, detected by addressing-mode usage patterns. " +
               "Returns FoundAt = -1 if none is found. " +
               "Returns 503 if no project is loaded.");

        // ── Diagnostics ──────────────────────────────────────────────────────

        api.MapPost("/diagnostics/rescan-inout-points", async () =>
           {
               await _service.RescanInOutPoints();
               return Results.Ok(new { ok = true });
           })
           .WithTags("Diagnostics")
           .WithName("RescanInOutPoints")
           .WithSummary("Rescan all in/out points across the ROM")
           .WithDescription(
               "Recomputes InPoint and OutPoint flags for every instruction in the loaded ROM. " +
               "This is equivalent to the 'Rescan In/Out Points' menu action. " +
               "Returns 503 if no project is loaded.");

        api.MapPost("/diagnostics/fix-misaligned-flags", async () =>
           {
               var count = await _service.FixMisalignedFlags();
               return Results.Ok(new { count });
           })
           .WithTags("Diagnostics")
           .WithName("FixMisalignedFlags")
           .WithSummary("Fix misaligned operand flags")
           .WithDescription(
               "Detects and corrects bytes that are incorrectly marked as Operand or Opcode due to " +
               "misalignment with their actual instruction boundary. " +
               "Returns the number of bytes corrected. " +
               "Returns 503 if no project is loaded.");

        api.MapGet("/diagnostics/misalignment-report", () => _service.GetMisalignmentReport())
           .WithTags("Diagnostics")
           .WithName("GetMisalignmentReport")
           .WithSummary("Generate a misalignment report without modifying data")
           .WithDescription(
               "Scans the ROM for misaligned flag boundaries and returns a count and a " +
               "human-readable log of the findings. Does not modify any data. " +
               "Returns 503 if no project is loaded.");

        api.MapPost("/diagnostics/normalize-wram-labels", async () =>
           {
               await _service.NormalizeWramLabels();
               return Results.Ok(new { ok = true });
           })
           .WithTags("Diagnostics")
           .WithName("NormalizeWramLabels")
           .WithSummary("Canonicalize WRAM mirror label addresses")
           .WithDescription(
               "Normalizes labels on WRAM mirror addresses to their canonical form. " +
               "Use after bulk label imports that may have used non-canonical WRAM addresses. " +
               "Returns 503 if no project is loaded.");

        api.MapPost("/diagnostics/fix-checksum", async () =>
           {
               await _service.FixChecksum();
               return Results.Ok(new { ok = true });
           })
           .WithTags("Diagnostics")
           .WithName("FixChecksum")
           .WithSummary("Recompute and write a valid ROM checksum")
           .WithDescription(
               "Recalculates the SNES header checksum and complement and writes them into the ROM data. " +
               "The project must be saved afterward for the corrected values to be persisted to disk. " +
               "Returns 503 if no project is loaded.");

        // ── Regions ──────────────────────────────────────────────────────────

        api.MapGet("/regions", () => _service.GetAllRegions())
           .WithTags("Regions")
           .WithName("GetAllRegions")
           .WithSummary("List all regions in the project")
           .WithDescription(
               "Returns every region defined in the loaded project. Regions are named SNES address ranges " +
               "used to organise the disassembly output and can optionally export to separate assembly files. " +
               "Returns 503 if no project is loaded.");

        api.MapGet("/regions/at/{snesAddress}", (string snesAddress) => _service.GetRegionAt(snesAddress))
           .WithTags("Regions")
           .WithName("GetRegionAt")
           .WithSummary("Get the highest-priority region containing a SNES address")
           .WithDescription(
               "Returns the region with the highest priority that contains the given SNES address. " +
               "Regions are named SNES address ranges used to organise the disassembly output and can " +
               "optionally export to separate assembly files. " +
               "Returns 404 if no region covers that address, 503 if no project is loaded.");

        api.MapPost("/regions", async (CreateRegionBody body) =>
               Results.Ok(await _service.CreateRegion(
                   body.StartSnesAddress, body.EndSnesAddress, body.RegionName,
                   body.ContextToApply, body.Priority, body.ExportSeparateFile)))

           .WithTags("Regions")
           .WithName("CreateRegion")
           .WithSummary("Create a new region")
           .WithDescription(
               "Creates a new named region covering the given SNES address range and adds it to the project. " +
               "Regions can optionally be marked to export to a separate assembly file. " +
               "Returns the created region including its assigned index. " +
               "Returns 503 if no project is loaded.");

        api.MapDelete("/regions/{index:int}", async (int index) =>
           {
               await _service.DeleteRegion(index);
               return Results.Ok(new { ok = true });
           })
           .WithTags("Regions")
           .WithName("DeleteRegion")
           .WithSummary("Delete a region by index")
           .WithDescription(
               "Removes the region at the given index from the project's region list. " +
               "Use GET /regions to find region indices. " +
               "Returns 404 if the index is out of range, 503 if no project is loaded.");
    }

    // HTTP-specific request body records
    private record SelectionBody(int PcOffset);
    private record FlagBody(string FlagType);
    private record SetCommentBody(string Text);
    private record MarkBody(string SnesStart, string FlagType, int Count);
    private record LabelPutBody(string Name, string Comment);
    private record PatchLabelBody(string? Name, string? Comment);
    private record CreateRegionBody(
        string StartSnesAddress, string EndSnesAddress, string RegionName,
        string ContextToApply, int Priority, bool ExportSeparateFile);
    private record OpenProjectBody(string Path);
    private record SetContextMappingBody(string NameOverride);
}
