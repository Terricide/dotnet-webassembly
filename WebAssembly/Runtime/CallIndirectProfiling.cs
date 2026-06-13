using System;
using System.Collections.Generic;
using System.Linq;

namespace WebAssembly.Runtime;

/// <summary>
/// Receives optional runtime profiling callbacks for compiled <c>call_indirect</c> sites.
/// </summary>
public interface ICallIndirectProfiler
{
    /// <summary>
    /// Records execution of a compiled <c>call_indirect</c> site.
    /// </summary>
    /// <param name="typeIndex">The wasm type index used by the call site.</param>
    /// <param name="tableIndex">The table index used by the call site.</param>
    /// <param name="elementIndex">The runtime table element index loaded by the call.</param>
    /// <param name="target">The raw delegate loaded from the table, if any.</param>
    /// <param name="functionIndex">The enclosing wasm function index.</param>
    /// <param name="instructionOffset">The byte offset of the <c>call_indirect</c> instruction within the module.</param>
    void Record(uint typeIndex, uint tableIndex, uint elementIndex, Delegate? target, uint functionIndex, long instructionOffset);
}

/// <summary>
/// Requests an opt-in guarded direct-call fast path for a compiled <c>call_indirect</c> remapper.
/// </summary>
public sealed class CallIndirectDirectCallHint
{
    /// <summary>
    /// Creates a new <see cref="CallIndirectDirectCallHint"/>.
    /// </summary>
    public CallIndirectDirectCallHint(uint typeIndex, uint tableIndex, uint elementIndex, uint functionIndex, long hotness = 0, uint? siteFunctionIndex = null, long? siteInstructionOffset = null)
    {
        this.TypeIndex = typeIndex;
        this.TableIndex = tableIndex;
        this.ElementIndex = elementIndex;
        this.FunctionIndex = functionIndex;
        this.Hotness = hotness;
        this.SiteFunctionIndex = siteFunctionIndex;
        this.SiteInstructionOffset = siteInstructionOffset;
    }

    /// <summary>
    /// The wasm type index used by the remapper.
    /// </summary>
    public uint TypeIndex { get; }

    /// <summary>
    /// The table index used by the remapper.
    /// </summary>
    public uint TableIndex { get; }

    /// <summary>
    /// The hot table element index observed for this remapper.
    /// </summary>
    public uint ElementIndex { get; }

    /// <summary>
    /// The wasm function index expected at <see cref="ElementIndex"/>.
    /// </summary>
    public uint FunctionIndex { get; }

    /// <summary>
    /// Optional hotness score used to order multiple hints for the same remapper.
    /// </summary>
    public long Hotness { get; }

    /// <summary>
    /// Optional enclosing wasm function index for a site-specific hint.
    /// </summary>
    public uint? SiteFunctionIndex { get; }

    /// <summary>
    /// Optional instruction byte offset for a site-specific hint.
    /// </summary>
    public long? SiteInstructionOffset { get; }
}

/// <summary>
/// Immutable snapshot of a profiled <c>call_indirect</c> site.
/// </summary>
public sealed class CallIndirectSiteProfile
{
    /// <summary>
    /// Creates a new <see cref="CallIndirectSiteProfile"/>.
    /// </summary>
    public CallIndirectSiteProfile(uint typeIndex, uint tableIndex, long totalHits, uint functionIndex, long instructionOffset, IReadOnlyList<CallIndirectTargetProfile> targets)
    {
        this.TypeIndex = typeIndex;
        this.TableIndex = tableIndex;
        this.TotalHits = totalHits;
        this.FunctionIndex = functionIndex;
        this.InstructionOffset = instructionOffset;
        this.Targets = targets ?? throw new ArgumentNullException(nameof(targets));
    }

    /// <summary>
    /// The wasm type index used by the call site.
    /// </summary>
    public uint TypeIndex { get; }

    /// <summary>
    /// The table index used by the call site.
    /// </summary>
    public uint TableIndex { get; }

    /// <summary>
    /// Total executions observed at this call site.
    /// </summary>
    public long TotalHits { get; }

    /// <summary>
    /// The enclosing wasm function index.
    /// </summary>
    public uint FunctionIndex { get; }

    /// <summary>
    /// The byte offset of the <c>call_indirect</c> instruction in the module.
    /// </summary>
    public long InstructionOffset { get; }

    /// <summary>
    /// Profiled target distribution for the site.
    /// </summary>
    public IReadOnlyList<CallIndirectTargetProfile> Targets { get; }

    /// <summary>
    /// Indicates whether the site only observed a single target index.
    /// </summary>
    public bool IsMonomorphic => this.Targets.Count <= 1;
}

/// <summary>
/// Immutable snapshot of one target observed at a profiled <c>call_indirect</c> site.
/// </summary>
public sealed class CallIndirectTargetProfile
{
    /// <summary>
    /// Creates a new <see cref="CallIndirectTargetProfile"/>.
    /// </summary>
    public CallIndirectTargetProfile(uint elementIndex, long hits, string? delegateType, string? methodName)
    {
        this.ElementIndex = elementIndex;
        this.Hits = hits;
        this.DelegateType = delegateType;
        this.MethodName = methodName;
    }

    /// <summary>
    /// The table element index used by the call.
    /// </summary>
    public uint ElementIndex { get; }

    /// <summary>
    /// Number of times this element index was observed.
    /// </summary>
    public long Hits { get; }

    /// <summary>
    /// The CLR delegate type observed for this target, if available.
    /// </summary>
    public string? DelegateType { get; }

    /// <summary>
    /// The CLR method observed for this target, if available.
    /// </summary>
    public string? MethodName { get; }
}

/// <summary>
/// Collects optional runtime profiling data for compiled <c>call_indirect</c> sites.
/// </summary>
public sealed class CallIndirectProfileCollector : ICallIndirectProfiler
{
    private readonly object sync = new();
    private readonly Dictionary<CallIndirectSiteKey, MutableSiteProfile> sites = [];

    /// <inheritdoc />
    public void Record(uint typeIndex, uint tableIndex, uint elementIndex, Delegate? target, uint functionIndex, long instructionOffset)
    {
        lock (this.sync)
        {
            var key = new CallIndirectSiteKey(typeIndex, tableIndex, functionIndex, instructionOffset);
            if (!this.sites.TryGetValue(key, out var site))
            {
                site = new MutableSiteProfile(typeIndex, tableIndex, functionIndex, instructionOffset);
                this.sites.Add(key, site);
            }

            site.TotalHits++;

            if (!site.Targets.TryGetValue(elementIndex, out var targetProfile))
            {
                targetProfile = new MutableTargetProfile(elementIndex);
                site.Targets.Add(elementIndex, targetProfile);
            }

            targetProfile.Hits++;
            if (target != null)
            {
                targetProfile.DelegateType ??= target.GetType().FullName ?? target.GetType().Name;
                targetProfile.MethodName ??= target.Method.ToString();
            }
        }
    }

    /// <summary>
    /// Returns a stable snapshot of all observed call sites, sorted by descending hit count.
    /// </summary>
    public IReadOnlyList<CallIndirectSiteProfile> Snapshot()
    {
        lock (this.sync)
        {
            return this.sites.Values
                .Select(site => new CallIndirectSiteProfile(
                    site.TypeIndex,
                    site.TableIndex,
                    site.TotalHits,
                    site.FunctionIndex,
                    site.InstructionOffset,
                    site.Targets.Values
                        .Select(target => new CallIndirectTargetProfile(target.ElementIndex, target.Hits, target.DelegateType, target.MethodName))
                        .OrderByDescending(target => target.Hits)
                        .ThenBy(target => target.ElementIndex)
                        .ToArray()))
                .OrderByDescending(site => site.TotalHits)
                .ThenBy(site => site.TableIndex)
                .ThenBy(site => site.TypeIndex)
                .ToArray();
        }
    }

    private readonly struct CallIndirectSiteKey(uint typeIndex, uint tableIndex, uint functionIndex, long instructionOffset) : IEquatable<CallIndirectSiteKey>
    {
        public readonly uint TypeIndex = typeIndex;
        public readonly uint TableIndex = tableIndex;
        public readonly uint FunctionIndex = functionIndex;
        public readonly long InstructionOffset = instructionOffset;

        public bool Equals(CallIndirectSiteKey other)
            => this.TypeIndex == other.TypeIndex
            && this.TableIndex == other.TableIndex
            && this.FunctionIndex == other.FunctionIndex
            && this.InstructionOffset == other.InstructionOffset;

        public override bool Equals(object? obj) => obj is CallIndirectSiteKey other && this.Equals(other);

        public override int GetHashCode()
            => unchecked((((int)this.TypeIndex * 397) ^ (int)this.TableIndex) * 397 ^ (int)this.FunctionIndex) * 397 ^ this.InstructionOffset.GetHashCode();
    }

    private sealed class MutableSiteProfile(uint typeIndex, uint tableIndex, uint functionIndex, long instructionOffset)
    {
        public readonly uint TypeIndex = typeIndex;
        public readonly uint TableIndex = tableIndex;
        public readonly uint FunctionIndex = functionIndex;
        public readonly long InstructionOffset = instructionOffset;
        public long TotalHits;
        public readonly Dictionary<uint, MutableTargetProfile> Targets = [];
    }

    private sealed class MutableTargetProfile(uint elementIndex)
    {
        public readonly uint ElementIndex = elementIndex;
        public long Hits;
        public string? DelegateType;
        public string? MethodName;
    }
}
