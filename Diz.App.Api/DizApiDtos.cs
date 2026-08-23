namespace Diz.App.Api;

/// <summary>Metadata for the currently loaded ROM project.</summary>
public record ProjectInfoDto(
    /// <summary>Path to the .diz or .dizraw project file on disk.</summary>
    string ProjectFileName,
    /// <summary>Decoded game title string from the SNES ROM header.</summary>
    string RomGameName,
    /// <summary>ROM mapping mode as a string (e.g. LoRom, HiRom, ExHiRom).</summary>
    string RomMapMode,
    /// <summary>ROM speed setting from the header (e.g. SlowRom, FastRom).</summary>
    string RomSpeed,
    /// <summary>Total ROM size in bytes.</summary>
    int RomSize,
    /// <summary>Number of 64 KB banks in the ROM.</summary>
    int BankCount,
    /// <summary>True if the ROM header checksum and complement are valid.</summary>
    bool ChecksumValid,
    /// <summary>True if the project has unsaved modifications.</summary>
    bool UnsavedChanges,
    /// <summary>
    /// Lowest bank byte of the project's canonical SNES address range, as a 2-digit
    /// uppercase hex string (e.g. "C0"). Mirror addresses passed to label/comment
    /// endpoints are canonicalised into [CanonicalBankLow, CanonicalBankHigh] before
    /// storage. Derived from the project's map mode and ROM speed — ask for it rather
    /// than assuming a fixed range, which differs between projects.
    /// </summary>
    string CanonicalBankLow,
    /// <summary>
    /// Highest bank byte of the project's canonical SNES address range, as a 2-digit
    /// uppercase hex string (e.g. "DF"). See CanonicalBankLow.
    /// </summary>
    string CanonicalBankHigh
);

/// <summary>The current cursor selection position.</summary>
public record SelectionDto(
    /// <summary>PC (file) offset of the selected byte.</summary>
    int PcOffset,
    /// <summary>Uppercase 6-digit hex string of the equivalent SNES address, e.g. "C14CE5". Null if no project is loaded.</summary>
    string? SnesAddress
);

/// <summary>Full metadata for a single ROM byte at a given PC offset.</summary>
public record ByteInfoDto(
    /// <summary>PC (file) offset of this byte.</summary>
    int PcOffset,
    /// <summary>Uppercase 6-digit hex string of the equivalent SNES address, e.g. "C14CE5".</summary>
    string SnesAddress,
    /// <summary>Classification flag for this byte (e.g. Unreached, Opcode, Operand, Data8Bit, Data16Bit, Pointer16Bit).</summary>
    string FlagType,
    /// <summary>In/out point flags for this byte (e.g. None, InPoint, OutPoint, EndPoint). Multiple values may be combined.</summary>
    string InOutPoint,
    /// <summary>Data bank register value assumed at this offset (0–255).</summary>
    int DataBank,
    /// <summary>Direct page register value assumed at this offset (0–65535).</summary>
    int DirectPage,
    /// <summary>M (memory/accumulator width) processor flag assumed at this offset. True = 8-bit, false = 16-bit.</summary>
    bool MFlag,
    /// <summary>X (index register width) processor flag assumed at this offset. True = 8-bit, false = 16-bit.</summary>
    bool XFlag,
    /// <summary>CPU architecture at this offset (e.g. Cpu65C816, Apuspc700, GpuSuperFX).</summary>
    string Arch,
    /// <summary>Human-readable disassembled instruction string for this byte.</summary>
    string InstructionStr,
    /// <summary>Total length in bytes of the instruction that starts at this offset. 0 or negative if unknown.</summary>
    int InstructionLength,
    /// <summary>Raw ROM byte value. Null if the offset is out of range.</summary>
    byte? RawByte
);

/// <summary>A contiguous slice of per-byte metadata from the ROM.</summary>
public record ByteRangeDto(
    /// <summary>PC (file) offset of the first byte in the range.</summary>
    int From,
    /// <summary>Actual number of bytes returned (may be less than requested if the range extends past the ROM boundary or was clamped to 1024).</summary>
    int Count,
    /// <summary>Per-byte metadata for each byte in the range.</summary>
    IReadOnlyList<ByteInfoDto> Bytes
);

/// <summary>A context-specific name override for a label.</summary>
public record ContextMappingDto(
    /// <summary>
    /// The context name. Matches against the ContextToApply field of a Region.
    /// </summary>
    string Context,
    /// <summary>
    /// The label name to use instead of the primary name when inside a
    /// region whose ContextToApply matches this Context.
    /// </summary>
    string NameOverride
);

/// <summary>A label annotation at a SNES address.</summary>
public record LabelDto(
    /// <summary>Uppercase 6-digit hex string of the SNES address this label is attached to, e.g. "C14CE5".</summary>
    string SnesAddress,
    /// <summary>Label name as it will appear in the disassembly output.</summary>
    string Name,
    /// <summary>Optional comment associated with this label.</summary>
    string Comment,
    /// <summary>
    /// Context-specific name overrides. Each entry applies within regions
    /// whose ContextToApply matches the entry's Context string.
    /// </summary>
    IReadOnlyList<ContextMappingDto> ContextMappings
);

/// <summary>A comment annotation at a SNES address.</summary>
public record CommentDto(
    /// <summary>Uppercase 6-digit hex string of the SNES address this comment is attached to, e.g. "C14CE5".</summary>
    string SnesAddress,
    /// <summary>Comment text.</summary>
    string Text
);

/// <summary>Result of a safe auto-step operation.</summary>
public record AutoStepResultDto(
    /// <summary>Uppercase 6-digit hex SNES address where auto-stepping began, e.g. "C14CE5".</summary>
    string StartedAtSnesAddress,
    /// <summary>PC (file) offset where auto-stepping began.</summary>
    int StartedAtPcOffset,
    /// <summary>Uppercase 6-digit hex SNES address where auto-stepping stopped. Null if it stopped past the end of the ROM.</summary>
    string? StoppedAtSnesAddress,
    /// <summary>PC (file) offset where auto-stepping stopped.</summary>
    int StoppedAtPcOffset,
    /// <summary>Flag type of the byte at the stop position (e.g. Opcode, Unreached). Null if it stopped past the end of the ROM.</summary>
    string? FlagType
);

/// <summary>The intermediate (branch/jump target) address for an instruction.</summary>
public record IntermediateAddressDto(
    /// <summary>Uppercase 6-digit hex SNES address of the instruction whose target was resolved, e.g. "C14CE5".</summary>
    string SnesAddress,
    /// <summary>PC (file) offset of the instruction whose target was resolved.</summary>
    int PcOffset,
    /// <summary>Uppercase 6-digit hex string of the resolved target SNES address (the branch/jump/pointer destination), e.g. "C14CE5".</summary>
    string IntermediateSnesAddress,
    /// <summary>Flag type of the instruction at SnesAddress (e.g. Opcode).</summary>
    string FlagType
);

/// <summary>
/// Response to a byte-comment write. Echoes the canonical address the comment was
/// stored at, plus the full metadata of the byte that was touched — so the caller can
/// verify what it just annotated without a follow-up read.
/// </summary>
public record CommentWriteDto(
    /// <summary>Uppercase 6-digit hex SNES address the comment was stored at, after mirror canonicalisation.</summary>
    string SnesAddress,
    /// <summary>The comment text as stored. Empty string means the comment was cleared.</summary>
    string Text,
    /// <summary>Full metadata for the annotated byte.</summary>
    ByteInfoDto Byte
);

/// <summary>
/// Response to a label write. Echoes the canonical address the label was stored at,
/// plus the metadata of the underlying ROM byte when the address is ROM-mapped.
/// </summary>
public record LabelWriteDto(
    /// <summary>Uppercase 6-digit hex SNES address the label was stored at, after mirror canonicalisation.</summary>
    string SnesAddress,
    /// <summary>Label name as it will appear in the disassembly output.</summary>
    string Name,
    /// <summary>Optional comment associated with this label.</summary>
    string Comment,
    /// <summary>Context-specific name overrides on this label.</summary>
    IReadOnlyList<ContextMappingDto> ContextMappings,
    /// <summary>
    /// Full metadata for the ROM byte under this label. Null when the label sits at a
    /// non-ROM address (WRAM, hardware register) — a legitimate equate with no ROM byte.
    /// </summary>
    ByteInfoDto? Byte
);

/// <summary>Response to a range classification write.</summary>
public record MarkResultDto(
    /// <summary>Uppercase 6-digit hex SNES address of the first byte marked, after canonicalisation.</summary>
    string SnesStart,
    /// <summary>PC (file) offset of the first byte marked.</summary>
    int PcOffset,
    /// <summary>The FlagType applied to every byte in the range.</summary>
    string FlagType,
    /// <summary>Number of bytes marked, counted forward in PC (file) order from PcOffset.</summary>
    int Count,
    /// <summary>Full metadata for the first byte in the marked range.</summary>
    ByteInfoDto FirstByte
);

/// <summary>Result of a search for the next unreached branch point.</summary>
public record NextUnreachedDto(
    /// <summary>PC (file) offset of the unreached byte found. -1 if none was found.</summary>
    int FoundAt,
    /// <summary>Uppercase 6-digit hex string of the SNES address of the unreached byte found, e.g. "C14CE5". Null if none was found.</summary>
    string? FoundAtSnes,
    /// <summary>Uppercase 6-digit hex string of the SNES address of the branch/jump instruction that leads to this unreached byte, e.g. "C14CE5". Null if not applicable or not found.</summary>
    string? BranchSourceSnes
);

/// <summary>Result of a pointer-table detection scan.</summary>
public record NextPointerTableDto(
    /// <summary>PC (file) offset of the detected pointer table. -1 if none was found.</summary>
    int FoundAt,
    /// <summary>Uppercase 6-digit hex string of the SNES address of the detected pointer table, e.g. "C14CE5". Null if none was found.</summary>
    string? FoundAtSnes
);

/// <summary>A named SNES address region used to organise the disassembly output.</summary>
public record RegionDto(
    /// <summary>Index of this region in the project's Regions list (used for deletion).</summary>
    int Index,
    /// <summary>Uppercase 6-digit hex string of the inclusive start SNES address of the region, e.g. "C14CE5".</summary>
    string StartSnesAddress,
    /// <summary>Uppercase 6-digit hex string of the exclusive end SNES address of the region, e.g. "C1FFFF".</summary>
    string EndSnesAddress,
    /// <summary>Unique name identifying this region in the disassembly output.</summary>
    string RegionName,
    /// <summary>Label context name to apply inside this region. Labels whose context matches this name will use their alternate name within the region.</summary>
    string ContextToApply,
    /// <summary>Priority used to resolve overlapping regions. Higher value wins.</summary>
    int Priority,
    /// <summary>If true, this region's output will be written to a separate assembly file on export.</summary>
    bool ExportSeparateFile,
    /// <summary>Region export type: "Assembly" (inline db), "Binary" (verbatim incbin), or "Asset" (typed codec).</summary>
    string ExportType,
    /// <summary>Dotted asset type (e.g. "gfx.snes.2bpp", "audio.brr", "blob.container"); empty for Assembly/Binary.</summary>
    string AssetType,
    /// <summary>Asset schema/version tag; empty when not an asset.</summary>
    string AssetVersion,
    /// <summary>Output asset name (drives the generated incbin filename); empty to derive from the region name.</summary>
    string AssetName,
    /// <summary>Free-form JSON options blob passed to the asset exporter; empty when not an asset.</summary>
    string AssetOptions
);

/// <summary>A report of flag-boundary misalignments in the ROM.</summary>
public record MisalignmentReportDto(
    /// <summary>Number of misaligned bytes detected.</summary>
    int Count,
    /// <summary>Human-readable log describing each misalignment found.</summary>
    string Log
);
