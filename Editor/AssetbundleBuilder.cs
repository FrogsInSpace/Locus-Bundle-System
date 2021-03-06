﻿using System.Linq;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.WriteTypes;
using UnityEngine;
using System;
using UnityEngine.SceneManagement;

namespace BundleSystem
{
    public enum BuildType
    {
        Remote,
        Local,
        Dry,
    }


    /// <summary>
    /// class that contains actual build functionalities
    /// </summary>
    public static class AssetbundleBuilder
    {
        const string LogFileName = "BundleBuildLog.txt";
        const string LogDependencyName = "BundleDependencyLog.txt";
        static AssetbundleBuildSettings s_CurrentBuildingSettings = null;
        static BuildType s_CurrentBuildType = BuildType.Dry;

        class CustomBuildParameters : BundleBuildParameters
        {
            AssetbundleBuildSettings m_Settings;
            bool m_IsLocal;

            public CustomBuildParameters(AssetbundleBuildSettings settings, BuildTarget target, BuildTargetGroup group, string outputFolder, bool isLocal) : base(target, group, outputFolder)
            {
                m_Settings = settings;
                m_IsLocal = isLocal;
            }

            // Override the GetCompressionForIdentifier method with new logic
            public override BuildCompression GetCompressionForIdentifier(string identifier)
            {
                //local bundles are always lz4 for faster initializing
                if (m_IsLocal) return BuildCompression.LZ4;

                //find user set compression method
                var found = m_Settings.BundleSettings.FirstOrDefault(setting => setting.BundleName == identifier);
                return found == null || !found.CompressBundle ? BuildCompression.LZ4 : BuildCompression.LZMA;
            }
        }

        public static void BuildAssetBundles(BuildType buildType)
        {
            var editorInstance = AssetbundleBuildSettings.EditorInstance;
            BuildAssetBundles(editorInstance, buildType);
        }

        public static void BuildAssetBundles(AssetbundleBuildSettings settings, BuildType buildType)
        {
            var bundleList = new List<AssetBundleBuild>();
            foreach (var setting in settings.BundleSettings)
            {
                var folderPath = AssetDatabase.GUIDToAssetPath(setting.Folder.guid);
                var dir = new DirectoryInfo(Path.Combine(Application.dataPath, folderPath.Remove(0, 7)));
                if (!dir.Exists) throw new Exception($"Could not found Path {folderPath} for {setting.BundleName}");
                var assetPathes = new List<string>();
                var loadPathes = new List<string>();
                AssetbundleBuildSettings.GetFilesInDirectory(string.Empty, assetPathes, loadPathes, dir, setting.IncludeSubfolder);
                if (assetPathes.Count == 0) Debug.LogWarning($"Could not found Any Assets {folderPath} for {setting.BundleName}");
                var newBundle = new AssetBundleBuild();
                newBundle.assetBundleName = setting.BundleName;
                newBundle.assetNames = assetPathes.ToArray();
                newBundle.addressableNames = loadPathes.ToArray();
                bundleList.Add(newBundle);
            }

            var buildTarget = EditorUserBuildSettings.activeBuildTarget;
            var groupTarget = BuildPipeline.GetBuildTargetGroup(buildTarget);

            var outputPath = Path.Combine(buildType == BuildType.Local ? settings.LocalOutputPath : settings.RemoteOutputPath, buildTarget.ToString());
            var buildParams = new CustomBuildParameters(settings, buildTarget, groupTarget, outputPath, buildType == BuildType.Local);

            buildParams.UseCache = !settings.ForceRebuild;

            if (buildParams.UseCache && settings.UseCacheServer)
            {
                buildParams.CacheServerHost = settings.CacheServerHost;
                buildParams.CacheServerPort = settings.CacheServerPort;
            }

            s_CurrentBuildingSettings = settings;
            s_CurrentBuildType = buildType;
            ContentPipeline.BuildCallbacks.PostPackingCallback += PostPackingForSelectiveBuild;
            var returnCode = ContentPipeline.BuildAssetBundles(buildParams, new BundleBuildContent(bundleList.ToArray()), out var results);
            ContentPipeline.BuildCallbacks.PostPackingCallback -= PostPackingForSelectiveBuild;
            
            if(buildType == BuildType.Dry)
            {
                EditorUtility.DisplayDialog("Build Succeeded!", "Dry bundle build succeeded!", "Confirm");
                return;
            }

            if (returnCode == ReturnCode.Success)
            {
                WriteManifestFile(outputPath, results, buildTarget, settings.RemoteURL);
                WriteLogFile(outputPath, results);
                //only remote bundle build generates link.xml

                switch(buildType)
                {
                    case BuildType.Local:
                        EditorUtility.DisplayDialog("Build Succeeded!", "Local bundle build succeeded!", "Confirm");
                        break;
                    case BuildType.Remote:
                        var linkPath = TypeLinkerGenerator.Generate(settings, results);
                        EditorUtility.DisplayDialog("Build Succeeded!", $"Remote bundle build succeeded, \n {linkPath} updated!", "Confirm");
                        break;
                    case BuildType.Dry:
                        EditorUtility.DisplayDialog("Build Succeeded!", $"Dry bundle build succeeded", "Confirm");
                        break;
                }
            }
            else
            {
                EditorUtility.DisplayDialog("Build Failed!", $"Bundle build failed, \n Code : {returnCode}", "Confirm");
                Debug.LogError(returnCode);
            }
        }

        private static ReturnCode PostPackingForSelectiveBuild(IBuildParameters buildParams, IDependencyData dependencyData, IWriteData writeData)
        {
            List<string> includedBundles;
            if(s_CurrentBuildType == BuildType.Local)
            {
                includedBundles = s_CurrentBuildingSettings.BundleSettings
                    .Where(setting => setting.IncludedInPlayer)
                    .Select(setting => setting.BundleName)
                    .ToList();
            }
            //if not local build, we include everything
            else
            {
                includedBundles = s_CurrentBuildingSettings.BundleSettings
                    .Select(setting => setting.BundleName)
                    .ToList();
            }

            //quick exit 
            if (includedBundles == null || includedBundles.Count == 0)
            {
                Debug.Log("Nothing to build");
                return ReturnCode.Success;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Build Time : {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}");
            sb.AppendLine();

            var bundleHashDic = new Dictionary<string, HashSet<GUID>>();

            for (int i = writeData.WriteOperations.Count - 1; i >= 0; --i)
            {
                string bundleName;
                HashSet<GUID> guidHashSet;
                switch (writeData.WriteOperations[i])
                {
                    case SceneBundleWriteOperation sceneOperation:
                        bundleName = sceneOperation.Info.bundleName;
                        if (!bundleHashDic.TryGetValue(bundleName, out guidHashSet))
                        {
                            guidHashSet = new HashSet<GUID>();
                            bundleHashDic.Add(bundleName, guidHashSet);
                        }

                        foreach (var bundleSceneInfo in sceneOperation.Info.bundleScenes)
                        {
                            guidHashSet.Add(bundleSceneInfo.asset);
                        }

                        foreach (var asset in sceneOperation.PreloadInfo.preloadObjects)
                        {
                            if (asset.fileType == UnityEditor.Build.Content.FileType.NonAssetType) continue;
                            guidHashSet.Add(asset.guid);
                        }

                        break;
                    case SceneDataWriteOperation sceneDataOperation:
                        var bundleWriteData = writeData as IBundleWriteData;
                        bundleName = bundleWriteData.FileToBundle[sceneDataOperation.Command.internalName];
                        if (!bundleHashDic.TryGetValue(bundleName, out guidHashSet))
                        {
                            guidHashSet = new HashSet<GUID>();
                            bundleHashDic.Add(bundleName, guidHashSet);
                        }
                        foreach (var identifier in sceneDataOperation.PreloadInfo.preloadObjects)
                        {
                            if (identifier.fileType == UnityEditor.Build.Content.FileType.NonAssetType) continue;
                            guidHashSet.Add(identifier.guid);
                        }
                        break;
                    case AssetBundleWriteOperation assetBundleOperation:
                        bundleName = assetBundleOperation.Info.bundleName;
                        if (!bundleHashDic.TryGetValue(bundleName, out guidHashSet))
                        {
                            guidHashSet = new HashSet<GUID>();
                            bundleHashDic.Add(bundleName, guidHashSet);
                        }

                        foreach (var bs in assetBundleOperation.Info.bundleAssets)
                        {
                            foreach (var asset in bs.includedObjects)
                            {
                                if (asset.fileType == UnityEditor.Build.Content.FileType.NonAssetType) continue;
                                guidHashSet.Add(asset.guid);
                            }

                            foreach (var asset in bs.referencedObjects)
                            {
                                if (asset.fileType == UnityEditor.Build.Content.FileType.NonAssetType) continue;
                                guidHashSet.Add(asset.guid);
                            }
                        }
                        break;
                    default:
                        Debug.LogError("Unexpected write operation");
                        return ReturnCode.Error;

                }

                // if we do not want to build that bundle, remove the write operation from the list
                if (!includedBundles.Contains(bundleName) || s_CurrentBuildType == BuildType.Dry)
                {
                    writeData.WriteOperations.RemoveAt(i);
                }
            }

            foreach (var kv in bundleHashDic)
            {
                sb.AppendLine($"----File Path : {kv.Key}----");

                foreach (var guid in kv.Value)
                {
                    sb.AppendLine($"{(GetCompressedFileSize(guid.ToString()) * 0.000001f).ToString("0.00000").PadLeft(10)} mb - {AssetDatabase.GUIDToAssetPath(guid.ToString())}");
                }

                sb.AppendLine();
            }

            var path = Application.dataPath + "/../";
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, LogDependencyName), sb.ToString());
            return ReturnCode.Success;
        }

        /// <summary>
        /// write manifest into target path.
        /// </summary>
        static void WriteManifestFile(string path, IBundleBuildResults bundleResults, BuildTarget target, string remoteURL)
        {
            var manifest = new AssetbundleBuildManifest();
            manifest.BuildTarget = target.ToString();
            var deps = bundleResults.BundleInfos.ToDictionary(kv => kv.Key, kv => kv.Value.Dependencies.ToList());
            var depsCollectCache = new HashSet<string>();

            foreach (var result in bundleResults.BundleInfos)
            {
                var bundleInfo = new AssetbundleBuildManifest.BundleInfo();
                bundleInfo.BundleName = result.Key;
                depsCollectCache.Clear();
                CollectBundleDependencies(depsCollectCache, deps, result.Key);
                bundleInfo.Dependencies = depsCollectCache.ToList();
                bundleInfo.Hash = result.Value.Hash;
                bundleInfo.Size = new FileInfo(result.Value.FileName).Length;
                manifest.BundleInfos.Add(bundleInfo);
            }

            //sort by size
            manifest.BundleInfos.Sort((a, b) => b.Size.CompareTo(a.Size));
            var manifestString = JsonUtility.ToJson(manifest);
            manifest.GlobalHash = Hash128.Compute(manifestString);
            manifest.BuildTime = DateTime.UtcNow.Ticks;
            manifest.RemoteURL = remoteURL;
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, AssetbundleBuildSettings.ManifestFileName), JsonUtility.ToJson(manifest, true));
        }

        public static long GetCompressedFileSize(string guid)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(assetPath))
            {
                string p = Path.GetFullPath(string.Format("{0}../../Library/metadata/{1}/{2}.resource", Application.dataPath, guid.Substring(0, 2), guid));
                if (File.Exists(p))
                {
                    return new FileInfo(p).Length;
                }
            }
            return 0;
        }

        /// <summary>
        /// write logs into target path.
        /// </summary>
        static void WriteLogFile(string path, IBundleBuildResults bundleResults)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Build Time : {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}");
            sb.AppendLine();
            
            for(int i = 0; i < bundleResults.BundleInfos.Count; i++)
            {
                var bundleInfo = bundleResults.BundleInfos.ElementAt(i);
                var writeResult = bundleResults.WriteResults.ElementAt(i);
                sb.AppendLine($"----File Path : {bundleInfo.Value.FileName}----");
                var assetDic = new Dictionary<string, ulong>();
                foreach(var file in writeResult.Value.serializedObjects)
                {
                    //skip nonassettype
                    if (file.serializedObject.fileType == UnityEditor.Build.Content.FileType.NonAssetType) continue;

                    //gather size
                    var assetPath = AssetDatabase.GUIDToAssetPath(file.serializedObject.guid.ToString());
                    if (!assetDic.ContainsKey(assetPath))
                    {
                        assetDic.Add(assetPath, file.header.size);
                    } 
                    else assetDic[assetPath] += file.header.size;
                }

                //sort by it's size
                var sortedAssets = assetDic.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key);

                foreach(var asset in sortedAssets)
                {
                    sb.AppendLine($"{(asset.Value * 0.000001f).ToString("0.00000").PadLeft(10)} mb - {asset.Key}");
                }

                sb.AppendLine();
            }

            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, LogFileName), sb.ToString());
        }

        static void CollectBundleDependencies(HashSet<string> result, Dictionary<string, List<string>> deps,  string name)
        {
            result.Add(name);
            foreach(var dependency in deps[name])
            {
                if (!result.Contains(dependency))
                    CollectBundleDependencies(result, deps, dependency);
            }
        }
    }
}
