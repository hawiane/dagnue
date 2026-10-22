using System.Numerics;
using Nexus.Core.Models;

namespace Nexus.Core.SystemServices;

/// <summary>
/// Builds a deterministic, parent-local layout for a filesystem tree. Directories
/// inherit a narrowing angular sector and files occupy a small disk around their
/// immediate directory. The index remains complete; this class only assigns space.
/// </summary>
public static class HierarchicalFileGraphLayout
{
    public static IReadOnlyList<Vector3> Build(FileGraphSnapshot snapshot)
    {
        if (snapshot.Nodes.Count == 0) return [];

        var count = snapshot.Nodes.Count;
        var positions = new Vector3[count];
        var headings = new float[count];
        var sectorWidths = new float[count];
        var subtreeCounts = Enumerable.Repeat(1L, count).ToArray();
        var children = new Dictionary<int, List<int>>();
        for (var index = count - 1; index >= 0; index--)
        {
            var parent = snapshot.Nodes[index].ParentId;
            if (parent is not { } parentIndex || parentIndex < 0 || parentIndex >= count) continue;
            if (!children.TryGetValue(parentIndex, out var siblings))
                children[parentIndex] = siblings = [];
            siblings.Add(index);
            subtreeCounts[parentIndex] = Math.Min(int.MaxValue,
                subtreeCounts[parentIndex] + subtreeCounts[index]);
        }

        var roots = snapshot.Nodes
            .Where(node => node.ParentId is null)
            .Select(node => node.Id)
            .Where(index => index >= 0 && index < count)
            .ToArray();
        for (var ordinal = 0; ordinal < roots.Length; ordinal++)
        {
            var index = roots[ordinal];
            var angle = roots.Length <= 1 ? 0f : -MathF.PI * 0.5f + ordinal * MathF.Tau / roots.Length;
            positions[index] = roots.Length <= 1
                ? Vector3.Zero
                : new Vector3(MathF.Cos(angle) * 0.38f, MathF.Sin(angle) * 0.31f,
                    MathF.Sin(angle * 2f) * 0.035f);
            headings[index] = angle;
            sectorWidths[index] = roots.Length <= 1
                ? MathF.Tau
                : MathF.Min(1.42f, MathF.Tau * 0.76f / roots.Length);
        }

        // FileGraphService emits parents before children, so this pass can place a
        // directory only after its own parent has received a sector.
        for (var parentIndex = 0; parentIndex < count; parentIndex++)
        {
            if (!snapshot.Nodes[parentIndex].IsDirectory ||
                !children.TryGetValue(parentIndex, out var parentChildren)) continue;
            var directoryChildren = parentChildren
                .Where(index => snapshot.Nodes[index].IsDirectory)
                .OrderByDescending(index => subtreeCounts[index])
                .ThenBy(index => snapshot.Nodes[index].Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            PlaceDirectories(parentIndex, directoryChildren, snapshot, subtreeCounts,
                positions, headings, sectorWidths);

            var fileChildren = parentChildren
                .Where(index => !snapshot.Nodes[index].IsDirectory)
                .Reverse()
                .ToArray();
            PlaceFiles(parentIndex, fileChildren, snapshot, positions);
        }

        var maximumLength = positions.Max(position => position.Length());
        if (maximumLength > 0.92f)
        {
            var scale = 0.92f / maximumLength;
            for (var index = 0; index < positions.Length; index++) positions[index] *= scale;
        }
        return positions;
    }

    private static void PlaceDirectories(int parentIndex, IReadOnlyList<int> directoryChildren,
        FileGraphSnapshot snapshot, IReadOnlyList<long> subtreeCounts, Vector3[] positions,
        float[] headings, float[] sectorWidths)
    {
        if (directoryChildren.Count == 0) return;
        var weights = directoryChildren
            .Select(index => MathF.Max(0.7f, MathF.Sqrt(MathF.Log2(subtreeCounts[index] + 1f))))
            .ToArray();
        var totalWeight = weights.Sum();
        var parentSector = sectorWidths[parentIndex];
        var usableSector = directoryChildren.Count == 1
            ? MathF.Min(parentSector, 0.62f)
            : parentSector * 0.91f;
        var childDepth = snapshot.Nodes[parentIndex].Depth + 1;
        var step = 0.225f * MathF.Pow(0.74f, Math.Min(8, Math.Max(0, childDepth - 1)));

        for (var ordinal = 0; ordinal < directoryChildren.Count; ordinal++)
        {
            var index = directoryChildren[ordinal];
            var allocation = usableSector * weights[ordinal] / MathF.Max(0.001f, totalWeight);
            // A van der Corput sequence spreads the most important (and therefore
            // first-rendered) siblings across the sector instead of packing them
            // into adjacent slots when a directory has thousands of children.
            var angle = directoryChildren.Count == 1
                ? headings[parentIndex]
                : headings[parentIndex] + (RadicalInverseBase2(ordinal + 1) - 0.5f) * usableSector;
            var separation = step * (0.88f + 0.12f * MathF.Min(1f, weights[ordinal] / 3f));
            var zBand = directoryChildren.Count == 1
                ? 0f
                : ((ordinal % 3) - 1) * separation * 0.16f;
            positions[index] = positions[parentIndex] + new Vector3(
                MathF.Cos(angle) * separation,
                MathF.Sin(angle) * separation,
                zBand);
            headings[index] = angle;
            sectorWidths[index] = MathF.Max(0.045f,
                MathF.Min(allocation * 0.82f, MathF.Min(0.92f, parentSector)));
        }
    }

    private static float RadicalInverseBase2(int value)
    {
        uint bits = (uint)value;
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x00ff00ffu) << 8) | ((bits & 0xff00ff00u) >> 8);
        bits = ((bits & 0x0f0f0f0fu) << 4) | ((bits & 0xf0f0f0f0u) >> 4);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xccccccccu) >> 2);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xaaaaaaaau) >> 1);
        return bits * 2.3283064365386963e-10f;
    }

    private static void PlaceFiles(int parentIndex, IReadOnlyList<int> fileChildren,
        FileGraphSnapshot snapshot, Vector3[] positions)
    {
        if (fileChildren.Count == 0) return;
        const float goldenAngle = 2.39996323f;
        var diskRadius = MathF.Min(0.056f,
            0.015f + MathF.Log2(fileChildren.Count + 1f) * 0.0048f);
        var depthScale = MathF.Pow(0.92f, Math.Min(8, snapshot.Nodes[parentIndex].Depth));
        diskRadius *= depthScale;

        for (var ordinal = 0; ordinal < fileChildren.Count; ordinal++)
        {
            var normalizedRadius = MathF.Sqrt((ordinal + 0.5f) / fileChildren.Count);
            var angle = ordinal * goldenAngle;
            var radius = diskRadius * normalizedRadius;
            positions[fileChildren[ordinal]] = positions[parentIndex] + new Vector3(
                MathF.Cos(angle) * radius,
                MathF.Sin(angle) * radius,
                MathF.Sin(angle * 0.5f) * diskRadius * 0.18f);
        }
    }
}
