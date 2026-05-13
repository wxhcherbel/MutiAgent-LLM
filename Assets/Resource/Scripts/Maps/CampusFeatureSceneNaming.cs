using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 地图要素场景命名工具。
/// 目标：让 JSON 导入、Unity 场景对象名、网格存储名、运行时查询名共用同一套规则。
/// </summary>
public static class CampusFeatureSceneNaming
{
    public static string SanitizeSceneName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;

        string sanitized = rawName.Trim()
            .Replace("\n", " ")
            .Replace("\r", " ")
            .Replace("\t", " ");

        char[] invalidChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        for (int i = 0; i < invalidChars.Length; i++)
        {
            sanitized = sanitized.Replace(invalidChars[i], '_');
        }

        return sanitized.Trim();
    }

    public static string GetDefaultScenePrefix(CampusFeatureKind kind)
    {
        return kind switch
        {
            CampusFeatureKind.Building => "building",
            CampusFeatureKind.Sports => "sports",
            CampusFeatureKind.Water => "water",
            CampusFeatureKind.Road => "road",
            CampusFeatureKind.Expressway => "expressway",
            CampusFeatureKind.Bridge => "bridge",
            CampusFeatureKind.Parking => "parking",
            CampusFeatureKind.Green => "green",
            CampusFeatureKind.Forest => "forest",
            _ => "green"
        };
    }

    public static string GetDefaultScenePrefix(string kindToken)
    {
        string normalizedKind = (kindToken ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedKind)) return "green";

        return normalizedKind switch
        {
            "building" => "building",
            "sports" => "sports",
            "water" => "water",
            "road" => "road",
            "expressway" => "expressway",
            "bridge" => "bridge",
            "parking" => "parking",
            "green" => "green",
            "forest" => "forest",
            _ => "green"
        };
    }

    public static string BuildPartSceneName(string featureSceneName, int partIndex, int partCount)
    {
        string baseName = SanitizeSceneName(featureSceneName);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "green_1";
        if (partCount <= 1) return baseName;
        return $"{baseName}_p{partIndex + 1}";
    }
}

/// <summary>
/// 负责在一次导入/建图过程中分配唯一的场景要素名称。
/// </summary>
public sealed class CampusFeatureSceneNameAllocator
{
    private readonly HashSet<string> usedSceneNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> kindCounters = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

    public string AllocateSceneName(string explicitName, string defaultPrefix)
    {
        string sanitizedExplicitName = CampusFeatureSceneNaming.SanitizeSceneName(explicitName);
        bool hasExplicitName = !string.IsNullOrWhiteSpace(sanitizedExplicitName) && sanitizedExplicitName != "-";

        string normalizedPrefix = CampusFeatureSceneNaming.SanitizeSceneName(defaultPrefix);
        if (string.IsNullOrWhiteSpace(normalizedPrefix))
        {
            normalizedPrefix = "green";
        }

        string baseName = hasExplicitName
            ? sanitizedExplicitName
            : $"{normalizedPrefix}_{NextIndex(normalizedPrefix)}";

        string finalName = baseName;
        int duplicateSuffix = 2;
        while (!usedSceneNames.Add(finalName))
        {
            finalName = $"{baseName}_{duplicateSuffix}";
            duplicateSuffix++;
        }

        if (!string.Equals(finalName, baseName, System.StringComparison.OrdinalIgnoreCase))
        {
            Debug.LogWarning($"[CampusFeatureNaming] 检测到地图重名要素，已自动改名: {baseName} -> {finalName}");
        }

        return finalName;
    }

    private int NextIndex(string prefix)
    {
        if (!kindCounters.TryGetValue(prefix, out int currentIndex))
        {
            currentIndex = 0;
        }

        currentIndex++;
        kindCounters[prefix] = currentIndex;
        return currentIndex;
    }
}
