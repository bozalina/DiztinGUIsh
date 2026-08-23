using System.Globalization;
using Diz.Controllers.interfaces;
using Diz.Core.Interfaces;
using Diz.Core.model;
using Diz.Cpu._65816;

namespace Diz.App.Api;

public class DizApiService(
    ICurrentProjectProvider projectProvider,
    ISelectionProvider selectionProvider,
    IUiThreadDispatcher dispatcher,
    IViewRefreshRequester refreshRequester,
    IProjectController projectController)
{
    private readonly ICurrentProjectProvider _projectProvider = projectProvider;
    private readonly ISelectionProvider _selectionProvider = selectionProvider;
    private readonly IUiThreadDispatcher _dispatcher = dispatcher;
    private readonly IViewRefreshRequester _refreshRequester = refreshRequester;
    private readonly IProjectController _projectController = projectController;

    /// <summary>
    /// Parses a SNES address from a hex string (e.g. "C14CE5" or "0xC14CE5").
    /// Case-insensitive. Throws DizApiException on invalid input or out-of-range value.
    /// </summary>
    internal static int ParseSnesAddress(string hex)
    {
        var s = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? hex[2..] : hex;
        if (!int.TryParse(s, NumberStyles.HexNumber, null, out var addr) || addr > 0xFFFFFF)
            throw new DizApiException(
                DizApiErrorKind.InvalidArgument,
                $"Invalid SNES address '{hex}' — expected a hex string such as 'C14CE5'.");
        return addr;
    }

    /// <summary>Formats a SNES address as an uppercase 6-digit hex string.</summary>
    internal static string FmtSnes(int addr) => addr.ToString("X6");

    private static IReadOnlyList<ContextMappingDto> MapContextMappings(IAnnotationLabel label) =>
        label.ContextMappings
            .Select(cm => new ContextMappingDto(cm.Context, cm.NameOverride))
            .ToList();

    // Helper — must be called from inside a dispatcher invocation
    private (Project project, ISnesData snesApi) RequireProject()
    {
        var project = _projectProvider.CurrentProject
            ?? throw new DizApiException(
                DizApiErrorKind.NoProjectLoaded, "No project loaded");
        var snesApi = project.Data.GetSnesApi()
            ?? throw new DizApiException(
                DizApiErrorKind.NoProjectLoaded, "No SNES data");
        return (project, snesApi);
    }

    // Converts a SNES address to its canonical form for label/comment storage.
    // ROM mirror addresses are normalised to the project's canonical bank range
    // (see GetProjectInfo) via a PC-offset round-trip. Non-ROM addresses
    // (WRAM, hardware registers, unmapped) are returned unchanged — labels
    // at those addresses are legitimate equates and must not be remapped.
    private static int CanonicalizeRomAddress(ISnesData snesApi, int snesAddress)
    {
        var pcOffset = snesApi.ConvertSnesToPc(snesAddress);
        if (pcOffset >= 0 && pcOffset < snesApi.GetRomSize())
            return snesApi.ConvertPCtoSnes(pcOffset);
        return snesAddress;
    }

    // Resolves a SNES address to the PC offset of the ROM byte it names, rejecting
    // any address with no ROM byte behind it.
    //
    // This is the guard for *byte-level* operations (flags, byte comments, marking,
    // auto-step, intermediate-address lookup): they act on a stored ROM byte, so an
    // address that maps nowhere in the file is a caller error, not an empty result.
    //
    // Label/equate operations deliberately do NOT call this — they use
    // CanonicalizeRomAddress, which passes non-ROM addresses (WRAM, hardware
    // registers) through untouched, because a label there is legitimate.
    //
    // ConvertSnesToPc rejects hardware registers and WRAM mirrors before its
    // map-mode switch runs (RomUtil.ConvertSnesToPcRaw), so this refuses exactly
    // the addresses a naive `snes & 0x3FFFFF` mask would silently corrupt.
    private static int RequireRomByte(ISnesData snesApi, int snesAddress)
    {
        var pcOffset = snesApi.ConvertSnesToPc(snesAddress);
        if (pcOffset < 0 || pcOffset >= snesApi.GetRomSize())
            throw new DizApiException(
                DizApiErrorKind.NotFound,
                $"No ROM byte exists at {snesAddress:X6} — byte-level operations require a " +
                $"ROM-mapped address (this may be WRAM, a hardware register, or unmapped). " +
                $"A label at {snesAddress:X6} is valid, though: use SetLabelAtAddress " +
                $"(PUT /labels/{snesAddress:X6}).");
        return pcOffset;
    }

    // Parse + validate in one step: the shape every byte-level annotation route uses.
    private static int RequireRomByte(ISnesData snesApi, string snesAddress) =>
        RequireRomByte(snesApi, ParseSnesAddress(snesAddress));

    // A label and a standalone byte comment may never coexist at the same
    // address. The two guards below enforce that — they take the already-resolved
    // canonical SNES address and throw a Conflict if the other kind is present.

    // Reject if a standalone byte comment already exists at this address.
    private static void EnsureNoByteComment(Project project, int canonicalSnes, string whatYouTried)
    {
        var existing = project.Data.GetComment(canonicalSnes);
        if (!string.IsNullOrEmpty(existing))
            throw new DizApiException(
                DizApiErrorKind.Conflict,
                $"{whatYouTried} a label at {canonicalSnes:X6} is blocked: a standalone " +
                $"byte comment already exists here. Delete the byte comment first " +
                $"(DeleteComment), then create the label with its comment. " +
                $"A label and a byte comment may not coexist at the same address.");
    }

    // Reject if a label already exists at this address.
    private static void EnsureNoLabel(ISnesData snesApi, int canonicalSnes)
    {
        if (snesApi.Labels.GetLabel(canonicalSnes) != null)
            throw new DizApiException(
                DizApiErrorKind.Conflict,
                $"Setting a byte comment at {canonicalSnes:X6} is blocked: a label " +
                $"already exists here. Put the comment on the label instead " +
                $"(PatchLabel with a comment, or SetLabelAtAddress). " +
                $"A label and a byte comment may not coexist at the same address.");
    }

    // Builds a comprehensive ByteInfoDto for the given PC offset
    private static ByteInfoDto BuildByteInfo(ISnesData snesApi, int pcOffset) =>
        new(
            PcOffset: pcOffset,
            SnesAddress: FmtSnes(snesApi.ConvertPCtoSnes(pcOffset)),
            FlagType: snesApi.GetFlag(pcOffset).ToString(),
            InOutPoint: snesApi.GetInOutPoint(pcOffset).ToString(),
            DataBank: snesApi.GetDataBank(pcOffset),
            DirectPage: snesApi.GetDirectPage(pcOffset),
            MFlag: snesApi.GetMFlag(pcOffset),
            XFlag: snesApi.GetXFlag(pcOffset),
            Arch: snesApi.Data.GetArchitecture(pcOffset).ToString(),
            InstructionStr: snesApi.GetInstructionStr(pcOffset),
            InstructionLength: snesApi.GetInstructionLength(pcOffset),
            RawByte: snesApi.GetRomByte(pcOffset)
        );

    // -------------------------------------------------------------------------
    // Project / selection
    // -------------------------------------------------------------------------

    public Task<ProjectInfoDto> GetProjectInfo() =>
        _dispatcher.InvokeAsync(() =>
        {
            var (project, snesApi) = RequireProject();
            var romSize = snesApi.GetRomSize();

            // The canonical range is a property of this project's map mode + ROM speed,
            // not a constant. HiRom/FastRom 2MB → C0..DF; WramImage → 7E..7F. Deriving it
            // from the round-trip keeps callers from memorising a range that rots.
            var lastOffset = Math.Max(0, romSize - 1);
            var bankLow  = (snesApi.ConvertPCtoSnes(0)          >> 16) & 0xFF;
            var bankHigh = (snesApi.ConvertPCtoSnes(lastOffset) >> 16) & 0xFF;

            return new ProjectInfoDto(
                ProjectFileName: project.Session?.ProjectFileName ?? "",
                RomGameName: snesApi.CartridgeTitleName,
                RomMapMode: snesApi.RomMapMode.ToString(),
                RomSpeed: snesApi.RomSpeed.ToString(),
                RomSize: romSize,
                BankCount: snesApi.GetNumberOfBanks(),
                ChecksumValid: snesApi.ComputeIsChecksumValid(),
                UnsavedChanges: project.Session?.UnsavedChanges ?? false,
                CanonicalBankLow: bankLow.ToString("X2"),
                CanonicalBankHigh: bankHigh.ToString("X2")
            );
        });

    public Task<SelectionDto> GetSelection() =>
        _dispatcher.InvokeAsync(() =>
        {
            var pcOffset = _selectionProvider.SelectedPcOffset;
            var project = _projectProvider.CurrentProject;
            var snesAddr = project?.Data.GetSnesApi()?.ConvertPCtoSnes(pcOffset);
            return new SelectionDto(
                PcOffset: pcOffset,
                SnesAddress: snesAddr.HasValue && snesAddr.Value >= 0
                    ? FmtSnes(snesAddr.Value)
                    : null
            );
        });

    public Task SetSelection(int pcOffset) =>
        _dispatcher.InvokeAsync(() =>
        {
            _selectionProvider.SelectedPcOffset = pcOffset;
        });

    // -------------------------------------------------------------------------
    // Byte access / mutation
    // -------------------------------------------------------------------------

    public Task<ByteInfoDto> GetByte(int pcOffset) =>
        _dispatcher.InvokeAsync(() =>
        {
            var (_, snesApi) = RequireProject();
            return BuildByteInfo(snesApi, pcOffset);
        });

    public Task<ByteInfoDto> GetByteBySnesAddress(string snesAddress) =>
        _dispatcher.InvokeAsync(() =>
        {
            var addr = ParseSnesAddress(snesAddress);
            var (_, snesApi) = RequireProject();
            var pcOffset = snesApi.ConvertSnesToPc(addr);
            if (pcOffset < 0 || pcOffset >= snesApi.GetRomSize())
                throw new DizApiException(
                    DizApiErrorKind.NotFound,
                    $"SNES address {snesAddress.ToUpperInvariant()} does not map to a ROM byte " +
                    $"(may be WRAM, hardware register, or unmapped)");
            return BuildByteInfo(snesApi, pcOffset);
        });

    public Task<ByteInfoDto> SetByteFlag(string snesAddress, string flagType) =>
        _dispatcher.InvokeAsync(() =>
        {
            var parsed = ParseFlagType(flagType);
            var (_, snesApi) = RequireProject();
            var pcOffset = RequireRomByte(snesApi, snesAddress);
            snesApi.SetFlag(pcOffset, parsed);
            _refreshRequester.RequestRefresh();
            return BuildByteInfo(snesApi, pcOffset);
        });

    public Task<CommentWriteDto> SetByteComment(string snesAddress, string text) =>
        _dispatcher.InvokeAsync(() =>
        {
            var (project, snesApi) = RequireProject();
            var pcOffset = RequireRomByte(snesApi, snesAddress);
            var canonical = snesApi.ConvertPCtoSnes(pcOffset);
            // Clearing a comment (empty text) is always allowed; only creating one
            // collides with a label at the same address.
            if (!string.IsNullOrEmpty(text))
                EnsureNoLabel(snesApi, canonical);
            project.Data.AddComment(canonical, text, overwrite: true);
            _refreshRequester.RequestRefresh();
            return new CommentWriteDto(
                FmtSnes(canonical), text, BuildByteInfo(snesApi, pcOffset));
        });

    public Task<MarkResultDto> MarkRange(string snesStart, string flagType, int count) =>
        _dispatcher.InvokeAsync(() =>
        {
            var parsed = ParseFlagType(flagType);
            if (count <= 0)
                throw new DizApiException(DizApiErrorKind.InvalidArgument,
                    $"'count' must be positive (got {count}).");
            var (_, snesApi) = RequireProject();
            var pcOffset = RequireRomByte(snesApi, snesStart);
            // Diz walks PC (file) order internally, so start+count stays well-defined
            // across map modes — which is why this takes a count, not an end address.
            snesApi.MarkTypeFlag(pcOffset, parsed, count);
            _refreshRequester.RequestRefresh();
            return new MarkResultDto(
                SnesStart: FmtSnes(snesApi.ConvertPCtoSnes(pcOffset)),
                PcOffset: pcOffset,
                FlagType: parsed.ToString(),
                Count: count,
                FirstByte: BuildByteInfo(snesApi, pcOffset));
        });

    private static FlagType ParseFlagType(string flagType) =>
        Enum.TryParse<FlagType>(flagType, ignoreCase: true, out var parsed)
            ? parsed
            : throw new DizApiException(
                DizApiErrorKind.InvalidArgument, $"Unknown flagType: {flagType}");

    // -------------------------------------------------------------------------
    // Range / subroutine queries
    // -------------------------------------------------------------------------

    public Task<ByteRangeDto> GetBytes(int from, int count) =>
        _dispatcher.InvokeAsync(() =>
        {
            var (_, snesApi) = RequireProject();
            var romSize = snesApi.GetRomSize();
            count = Math.Min(count, 1024);
            var end = Math.Min(from + count, romSize);
            var actualCount = Math.Max(end - from, 0);

            var bytes = new List<ByteInfoDto>(actualCount);
            for (var i = from; i < end; i++)
                bytes.Add(BuildByteInfo(snesApi, i));

            return new ByteRangeDto(from, actualCount, bytes);
        });

    // -------------------------------------------------------------------------
    // Label / comment queries
    // -------------------------------------------------------------------------

    public Task<IReadOnlyList<LabelDto>> GetAllLabels() =>
        _dispatcher.InvokeAsync(() =>
        {
            var (_, snesApi) = RequireProject();
            IReadOnlyList<LabelDto> labels = snesApi.Labels.Labels
                .Select(kvp => new LabelDto(FmtSnes(kvp.Key), kvp.Value.Name, kvp.Value.Comment, MapContextMappings(kvp.Value)))
                .ToList();
            return labels;
        });

    public Task<LabelDto> GetLabel(string snesAddress) =>
        _dispatcher.InvokeAsync(() =>
        {
            var addr = ParseSnesAddress(snesAddress);
            var (_, snesApi) = RequireProject();
            var canonical = CanonicalizeRomAddress(snesApi, addr);
            var label = snesApi.Labels.GetLabel(canonical)
                ?? throw new DizApiException(
                    DizApiErrorKind.NotFound,
                    $"No label at SNES address {snesAddress.ToUpperInvariant()}");
            return new LabelDto(FmtSnes(canonical), label.Name, label.Comment, MapContextMappings(label));
        });

    public Task<LabelDto> DeleteLabel(string snesAddress) =>
        _dispatcher.InvokeAsync(() =>
        {
            var addr = ParseSnesAddress(snesAddress);
            var (project, snesApi) = RequireProject();
            var canonical = CanonicalizeRomAddress(snesApi, addr);
            var existing = snesApi.Labels.GetLabel(canonical)
                ?? throw new DizApiException(
                    DizApiErrorKind.NotFound,
                    $"No label at {snesAddress.ToUpperInvariant()}.");
            var snapshot = new LabelDto(
                FmtSnes(canonical),
                existing.Name,
                existing.Comment,
                MapContextMappings(existing));
            project.Data.Labels.RemoveLabel(canonical);
            _refreshRequester.RequestRefresh();
            return snapshot;
        });

    public Task<LabelWriteDto> SetLabelAtAddress(string snesAddress, string name, string comment) =>
        _dispatcher.InvokeAsync(() =>
        {
            var addr = ParseSnesAddress(snesAddress);
            var (project, snesApi) = RequireProject();
            // Labels are equates: a non-ROM address (WRAM, hardware register) is
            // legitimate here, so this canonicalises rather than validating.
            var canonical = CanonicalizeRomAddress(snesApi, addr);
            EnsureNoByteComment(project, canonical, "Setting");
            project.Data.Labels.AddLabel(
                canonical,
                new Label { Name = name, Comment = comment },
                overwrite: true);
            _refreshRequester.RequestRefresh();
            var stored = snesApi.Labels.GetLabel(canonical);
            return new LabelWriteDto(
                FmtSnes(canonical), name, comment,
                stored != null ? MapContextMappings(stored) : [],
                TryBuildByteInfo(snesApi, canonical));
        });

    // Byte metadata for a SNES address, or null when it names no ROM byte.
    // Used by label writes, which legitimately target non-ROM addresses.
    private static ByteInfoDto? TryBuildByteInfo(ISnesData snesApi, int snesAddress)
    {
        var pcOffset = snesApi.ConvertSnesToPc(snesAddress);
        return pcOffset >= 0 && pcOffset < snesApi.GetRomSize()
            ? BuildByteInfo(snesApi, pcOffset)
            : null;
    }

    public Task<string?> SaveProject() =>
        _dispatcher.InvokeAsync<string?>(async () =>
        {
            var (project, _) = RequireProject();
            var filename = project.ProjectFileName;
            if (string.IsNullOrEmpty(filename))
                throw new DizApiException(
                    DizApiErrorKind.InvalidArgument,
                    "Project has not been saved to disk yet — no filename is set.");
            var error = await _projectController.SaveProjectAsync(filename);
            if (error != null)
                throw new DizApiException(DizApiErrorKind.InvalidArgument,
                    $"Save failed: {error}");
            return filename;
        });

    public Task OpenProject(string path) =>
        _dispatcher.InvokeAsync<bool>(async () =>
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new DizApiException(
                    DizApiErrorKind.InvalidArgument,
                    "'path' must not be empty.");

            var success = await _projectController.OpenProjectAsync(path);
            if (!success)
                throw new DizApiException(
                    DizApiErrorKind.NotFound,
                    $"Failed to open project '{path}'. " +
                    $"Check that the file exists and is a valid .dizraw project.");
            return success;
        });

    public Task<LabelDto> PatchLabel(string snesAddress, string? name, string? comment) =>
        _dispatcher.InvokeAsync(() =>
        {
            var addr = ParseSnesAddress(snesAddress);
            var (project, snesApi) = RequireProject();
            if (name == null && comment == null)
                throw new DizApiException(
                    DizApiErrorKind.InvalidArgument,
                    "At least one of 'name' or 'comment' must be provided.");
            var canonical  = CanonicalizeRomAddress(snesApi, addr);
            var existing   = snesApi.Labels.GetLabel(canonical);
            // Editing an existing label is always fine; only guard when PatchLabel
            // would create a brand-new label where a byte comment already lives.
            if (existing == null)
                EnsureNoByteComment(project, canonical, "Creating");
            var newName    = name    ?? existing?.Name    ?? "";
            var newComment = comment ?? existing?.Comment ?? "";
            project.Data.Labels.AddLabel(
                canonical,
                new Label { Name = newName, Comment = newComment },
                overwrite: true);
            var patchedLabel = snesApi.Labels.GetLabel(canonical);
            return new LabelDto(FmtSnes(canonical), newName, newComment,
                patchedLabel != null ? MapContextMappings(patchedLabel) : []);
        });

    public Task<LabelDto> SetLabelContextMapping(
        string snesAddress, string context, string nameOverride) =>
        _dispatcher.InvokeAsync(() =>
        {
            var addr = ParseSnesAddress(snesAddress);
            var (_, snesApi) = RequireProject();
            var canonical = CanonicalizeRomAddress(snesApi, addr);
            var label = snesApi.Labels.GetLabel(canonical)
                ?? throw new DizApiException(
                    DizApiErrorKind.NotFound,
                    $"No label at {snesAddress.ToUpperInvariant()}. " +
                    $"Create the label first with SetLabelAtAddress.");
            var existing = label.ContextMappings
                .FirstOrDefault(cm => cm.Context == context);
            if (existing != null)
                existing.NameOverride = nameOverride;
            else
                label.ContextMappings.Add(
                    new ContextMapping { Context = context, NameOverride = nameOverride });
            return new LabelDto(
                FmtSnes(canonical), label.Name, label.Comment,
                MapContextMappings(label));
        });

    public Task<LabelDto> RemoveLabelContextMapping(
        string snesAddress, string context) =>
        _dispatcher.InvokeAsync(() =>
        {
            var addr = ParseSnesAddress(snesAddress);
            var (_, snesApi) = RequireProject();
            var canonical = CanonicalizeRomAddress(snesApi, addr);
            var label = snesApi.Labels.GetLabel(canonical)
                ?? throw new DizApiException(
                    DizApiErrorKind.NotFound,
                    $"No label at {snesAddress.ToUpperInvariant()}.");
            var existing = label.ContextMappings
                .FirstOrDefault(cm => cm.Context == context);
            if (existing == null)
                throw new DizApiException(
                    DizApiErrorKind.NotFound,
                    $"No context mapping '{context}' on label at " +
                    $"{snesAddress.ToUpperInvariant()}.");
            label.ContextMappings.Remove(existing);
            return new LabelDto(
                FmtSnes(canonical), label.Name, label.Comment,
                MapContextMappings(label));
        });

    public Task<CommentDto> GetComment(string snesAddress) =>
        _dispatcher.InvokeAsync(() =>
        {
            var addr = ParseSnesAddress(snesAddress);
            var (project, snesApi) = RequireProject();
            var canonical = CanonicalizeRomAddress(snesApi, addr);
            var text = project.Data.GetComment(canonical);
            if (text == null)
                throw new DizApiException(
                    DizApiErrorKind.NotFound,
                    $"No comment at SNES address {snesAddress.ToUpperInvariant()}");
            return new CommentDto(FmtSnes(canonical), text);
        });

    public Task<IReadOnlyList<CommentDto>> GetAllComments() =>
        _dispatcher.InvokeAsync(() =>
        {
            var (project, _) = RequireProject();
            IReadOnlyList<CommentDto> comments = project.Data.Comments
                .Select(kvp => new CommentDto(FmtSnes(kvp.Key), kvp.Value))
                .ToList();
            return comments;
        });

    public Task<CommentDto> DeleteComment(string snesAddress) =>
        _dispatcher.InvokeAsync(() =>
        {
            var addr = ParseSnesAddress(snesAddress);
            var (project, snesApi) = RequireProject();
            var canonical = CanonicalizeRomAddress(snesApi, addr);
            var text = project.Data.GetComment(canonical)
                ?? throw new DizApiException(
                    DizApiErrorKind.NotFound,
                    $"No comment at SNES address {snesAddress.ToUpperInvariant()}");
            var snapshot = new CommentDto(FmtSnes(canonical), text);
            project.Data.AddComment(canonical, null, overwrite: true);
            _refreshRequester.RequestRefresh();
            return snapshot;
        });

    // -------------------------------------------------------------------------
    // Diagnostics
    // -------------------------------------------------------------------------

    public Task NormalizeWramLabels() =>
        _dispatcher.InvokeAsync(() =>
        {
            var (_, snesApi) = RequireProject();
            snesApi.NormalizeWramLabels();
            _refreshRequester.RequestRefresh();
        });

    public Task FixChecksum() =>
        _dispatcher.InvokeAsync(() =>
        {
            var (_, snesApi) = RequireProject();
            snesApi.FixChecksum();
            _refreshRequester.RequestRefresh();
        });

    public Task RescanInOutPoints() =>
        _dispatcher.InvokeAsync(() =>
        {
            var (_, snesApi) = RequireProject();
            snesApi.RescanInOutPoints();
            _refreshRequester.RequestRefresh();
        });

    public Task<int> FixMisalignedFlags() =>
        _dispatcher.InvokeAsync(() =>
        {
            var (_, snesApi) = RequireProject();
            var result = snesApi.FixMisalignedFlags();
            _refreshRequester.RequestRefresh();
            return result;
        });

    public Task<MisalignmentReportDto> GetMisalignmentReport() =>
        _dispatcher.InvokeAsync(() =>
        {
            var (_, snesApi) = RequireProject();
            var (found, outputTextLog) = snesApi.GenerateMisalignmentReport();
            return new MisalignmentReportDto(found, outputTextLog);
        });

    // -------------------------------------------------------------------------
    // Navigation / stepping
    // -------------------------------------------------------------------------

    public Task<AutoStepResultDto> AutoStep(string snesAddress) =>
        _dispatcher.InvokeAsync(() =>
        {
            var (_, snesApi) = RequireProject();
            var pcOffset = RequireRomByte(snesApi, snesAddress);
            var stoppedAt = snesApi.AutoStepSafe(pcOffset);
            _refreshRequester.RequestRefresh();

            // Auto-step can walk off the end of the ROM; don't convert or read a flag
            // from an offset that no longer names a byte.
            var stoppedInRange = stoppedAt >= 0 && stoppedAt < snesApi.GetRomSize();
            return new AutoStepResultDto(
                StartedAtSnesAddress: FmtSnes(snesApi.ConvertPCtoSnes(pcOffset)),
                StartedAtPcOffset: pcOffset,
                StoppedAtSnesAddress: stoppedInRange
                    ? FmtSnes(snesApi.ConvertPCtoSnes(stoppedAt)) : null,
                StoppedAtPcOffset: stoppedAt,
                FlagType: stoppedInRange ? snesApi.GetFlag(stoppedAt).ToString() : null
            );
        });

    public Task<IntermediateAddressDto> GetIntermediateAddress(string snesAddress) =>
        _dispatcher.InvokeAsync(() =>
        {
            var (_, snesApi) = RequireProject();
            var pcOffset = RequireRomByte(snesApi, snesAddress);
            var ia = snesApi.GetIntermediateAddressOrPointer(pcOffset);
            if (ia == -1)
                throw new DizApiException(
                    DizApiErrorKind.NotFound,
                    $"No intermediate address at {snesAddress.ToUpperInvariant()} — " +
                    $"this byte is neither a branch/jump/call instruction nor pointer data.");
            return new IntermediateAddressDto(
                SnesAddress: FmtSnes(snesApi.ConvertPCtoSnes(pcOffset)),
                PcOffset: pcOffset,
                IntermediateSnesAddress: FmtSnes(ia),
                FlagType: snesApi.GetFlag(pcOffset).ToString()
            );
        });

    // -------------------------------------------------------------------------
    // Regions
    // -------------------------------------------------------------------------

    private static RegionDto RegionToDto(int index, IRegion r) =>
        new(
            Index: index,
            StartSnesAddress: FmtSnes(r.StartSnesAddress),
            EndSnesAddress: FmtSnes(r.EndSnesAddress),
            RegionName: r.RegionName,
            ContextToApply: r.ContextToApply,
            Priority: r.Priority,
            ExportSeparateFile: r.ExportSeparateFile
        );

    public Task<IReadOnlyList<RegionDto>> GetAllRegions() =>
        _dispatcher.InvokeAsync(() =>
        {
            var (_, snesApi) = RequireProject();
            IReadOnlyList<RegionDto> regions = snesApi.Regions
                .Select((r, i) => RegionToDto(i, r))
                .ToList();
            return regions;
        });

    public Task<RegionDto> GetRegionAt(string snesAddress) =>
        _dispatcher.InvokeAsync(() =>
        {
            var addr = ParseSnesAddress(snesAddress);
            var (_, snesApi) = RequireProject();
            var region = snesApi.GetRegion(addr)
                ?? throw new DizApiException(DizApiErrorKind.NotFound,
                    $"No region at SNES address {snesAddress.ToUpperInvariant()}");
            var index = snesApi.Regions.IndexOf(region);
            return RegionToDto(index, region);
        });

    public Task<RegionDto> CreateRegion(
        string startSnesAddress, string endSnesAddress, string regionName,
        string contextToApply, int priority, bool exportSeparateFile) =>
        _dispatcher.InvokeAsync(() =>
        {
            var startAddr = ParseSnesAddress(startSnesAddress);
            var endAddr   = ParseSnesAddress(endSnesAddress);
            var (_, snesApi) = RequireProject();
            var region = snesApi.CreateNewRegion()
                ?? throw new DizApiException(DizApiErrorKind.NoProjectLoaded, "Failed to create region");
            region.StartSnesAddress = startAddr;
            region.EndSnesAddress   = endAddr;
            region.RegionName       = regionName;
            region.ContextToApply   = contextToApply;
            region.Priority         = priority;
            region.ExportSeparateFile = exportSeparateFile;
            snesApi.Regions.Add(region);
            var index = snesApi.Regions.Count - 1;
            var dto = RegionToDto(index, region);
            _refreshRequester.RequestRefresh();
            return dto;
        });

    public Task DeleteRegion(int index) =>
        _dispatcher.InvokeAsync(() =>
        {
            var (_, snesApi) = RequireProject();
            if (index < 0 || index >= snesApi.Regions.Count)
                throw new DizApiException(DizApiErrorKind.NotFound,
                    $"No region at index {index}");
            snesApi.Regions.RemoveAt(index);
            _refreshRequester.RequestRefresh();
        });

    public Task<NextUnreachedDto> FindNextUnreached(int from, bool searchForward) =>
        _dispatcher.InvokeAsync(() =>
        {
            var (_, snesApi) = RequireProject();
            var (foundOffset, iaSourceOffsetPc) = snesApi.FindNextUnreachedBranchPointAfter(from, searchForward);
            return new NextUnreachedDto(
                FoundAt: foundOffset,
                FoundAtSnes: foundOffset >= 0 ? FmtSnes(snesApi.ConvertPCtoSnes(foundOffset)) : null,
                BranchSourceSnes: iaSourceOffsetPc >= 0 ? FmtSnes(snesApi.ConvertPCtoSnes(iaSourceOffsetPc)) : null
            );
        });

    public Task<NextPointerTableDto> DetectNextPointerTable(int from, bool searchForward = true) =>
        _dispatcher.InvokeAsync(() =>
        {
            var (_, snesApi) = RequireProject();
            var foundOffset = snesApi.DetectNextPointerTableFromAddressingModeUsageAfter(from, searchForward);
            return new NextPointerTableDto(
                FoundAt: foundOffset,
                FoundAtSnes: foundOffset >= 0 ? FmtSnes(snesApi.ConvertPCtoSnes(foundOffset)) : null
            );
        });
}
