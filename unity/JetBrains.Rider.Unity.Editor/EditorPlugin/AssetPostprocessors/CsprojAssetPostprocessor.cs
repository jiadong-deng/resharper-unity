using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using JetBrains.Util;
using JetBrains.Util.Logging;
using UnityEditor;
using UnityEngine;

namespace JetBrains.Rider.Unity.Editor.AssetPostprocessors
{
  public class CsprojAssetPostprocessor : AssetPostprocessor
  {
    private static readonly ILog ourLogger = Log.GetLog<CsprojAssetPostprocessor>();

    public override int GetPostprocessOrder()
    {
      return 10;
    }

    public static void OnGeneratedCSProjectFiles()
    {
      if (!PluginEntryPoint.Enabled)
        return;

      // get only csproj files, which are mentioned in sln
      var lines = SlnAssetPostprocessor.GetCsprojLinesInSln();
      var currentDirectory = Directory.GetCurrentDirectory();
      var projectFiles = Directory.GetFiles(currentDirectory, "*.csproj")
        .Where(csprojFile => lines.Any(line => line.Contains("\"" + Path.GetFileName(csprojFile) + "\""))).ToArray();

      foreach (var file in projectFiles)
      {
        UpgradeProjectFile(file);
      }
    }

    private static void UpgradeProjectFile(string projectFile)
    {
      ourLogger.Verbose("Post-processing {0}", projectFile);
      XDocument doc;
      try
      {
        doc = XDocument.Load(projectFile);
      }
      catch (Exception)
      {
        ourLogger.Verbose("Failed to Load {0}", projectFile);
        return;
      }

      var projectContentElement = doc.Root;
      XNamespace xmlns = projectContentElement.Name.NamespaceName; // do not use var

      FixTargetFrameworkVersion(projectContentElement, xmlns);
      FixSystemXml(projectContentElement, xmlns);
      SetLangVersion(projectContentElement, xmlns);
      SetProjectFlavour(projectContentElement, xmlns);

      // Unity_5_6_OR_NEWER switched to nunit 3.5
      if (UnityUtils.UnityVersion >= new Version(5,6))
        ChangeNunitReference(projectContentElement, xmlns);

      //#i f !UNITY_2017_1_OR_NEWER // Unity 2017.1 and later has this features by itself
      if (UnityUtils.UnityVersion < new Version(2017, 1))
      {
        SetManuallyDefinedComilingSettings(projectFile, projectContentElement, xmlns);
      }
      SetXCodeDllReference("UnityEditor.iOS.Extensions.Xcode.dll", xmlns, projectContentElement);
      SetXCodeDllReference("UnityEditor.iOS.Extensions.Common.dll", xmlns, projectContentElement);

      ApplyManualCompilingSettingsReferences(projectContentElement, xmlns);
      doc.Save(projectFile);
    }

    private static void FixSystemXml(XElement projectContentElement, XNamespace xmlns)
    {
      var el = projectContentElement
        .Elements(xmlns+"ItemGroup")
        .Elements(xmlns+"Reference")
        .FirstOrDefault(a => a.Attribute("Include") !=null && a.Attribute("Include").Value=="System.XML");
      if (el != null)
      {
        el.Attribute("Include").Value = "System.Xml";
      }
    }

    private static void ChangeNunitReference(XElement projectContentElement, XNamespace xmlns)
    {
      var el = projectContentElement
        .Elements(xmlns + "ItemGroup")
        .Elements(xmlns + "Reference")
        .FirstOrDefault(a => a.Attribute("Include") != null && a.Attribute("Include").Value == "nunit.framework");

      if (el == null)
        return;

      var hintPath = el.Elements(xmlns + "HintPath").FirstOrDefault();
      if (hintPath == null)
        return;

      var path = Path.GetFullPath("Library/resharper-unity-libs/nunit3.5.0/nunit.framework.dll");
      InstallFromResource(path, ".nunit.framework.dll");
      if (new FileInfo(path).Exists)
        hintPath.Value = path;
    }

    private static void InstallFromResource(string fullPath, string namespacePath)
    {
      var targetFileInfo = new FileInfo(fullPath);
      if (targetFileInfo.Exists)
      {
        ourLogger.Log(LoggingLevel.VERBOSE, $"Already exists {targetFileInfo}");
        return;
      }

      var ass = Assembly.GetExecutingAssembly();
      ourLogger.Verbose("resources in {0}: {1}", ass.Location, ass.GetManifestResourceNames().Aggregate((a,b)=>a+", "+b));

      var resourceName = typeof(PluginEntryPoint).Namespace + namespacePath;

      try
      {
        using (var resourceStream = ass.GetManifestResourceStream(resourceName))
        {
          if (resourceStream == null)
            ourLogger.Error("Plugin file not found in manifest resources. " + resourceName);
          else
          {
            targetFileInfo.Directory.Create();
            using (var fileStream = new FileStream(targetFileInfo.FullName, FileMode.Create))
            {
              ourLogger.Verbose("Coping {0} => {1} ", resourceName, targetFileInfo);
              PdbAssetPostprocessor.CopyStream(resourceStream, fileStream);
            }
          }
        }
      }
      catch (Exception e)
      {
        ourLogger.Verbose(e.ToString());
        ourLogger.Warn($"{targetFileInfo} was not restored from resourse.");
      }
    }


    private static readonly string PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH = Path.GetFullPath("Assets/mcs.rsp");
    private const string UNITY_PLAYER_PROJECT_NAME = "Assembly-CSharp.csproj";
    private const string UNITY_EDITOR_PROJECT_NAME = "Assembly-CSharp-Editor.csproj";
    private const string UNITY_UNSAFE_KEYWORD = "-unsafe";
    private const string UNITY_DEFINE_KEYWORD = "-define:";
    private static readonly string  PLAYER_PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH = Path.GetFullPath("Assets/smcs.rsp");
    private static readonly string  EDITOR_PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH = Path.GetFullPath("Assets/gmcs.rsp");

    private static void SetManuallyDefinedComilingSettings(string projectFile, XElement projectContentElement, XNamespace xmlns)
    {
      string configPath = null;

      //Prefer mcs.rsp if it exists
      if (File.Exists(PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH))
      {
        configPath = PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH;
      }
      else
      {
        if (IsPlayerProjectFile(projectFile))
          configPath = PLAYER_PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH;
        else if (IsEditorProjectFile(projectFile))
          configPath = EDITOR_PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH;
      }

      if (!string.IsNullOrEmpty(configPath))
        ApplyManualCompilingSettings(configPath, projectContentElement, xmlns);
    }

    private static void ApplyManualCompilingSettings(string configFilePath, XElement projectContentElement, XNamespace xmlns)
    {
      if (File.Exists(configFilePath))
      {
        var configText = File.ReadAllText(configFilePath);
        if (configText.Contains(UNITY_UNSAFE_KEYWORD))
        {
          // Add AllowUnsafeBlocks to the .csproj. Unity doesn't generate it (although VSTU does).
          // Strictly necessary to compile unsafe code
          ApplyAllowUnsafeBlocks(projectContentElement, xmlns);
        }
        if (configText.Contains(UNITY_DEFINE_KEYWORD))
        {
          // defines could be
          // 1) -define:DEFINE1,DEFINE2
          // 2) -define:DEFINE1;DEFINE2
          // 3) -define:DEFINE1 -define:DEFINE2
          // 4) -define:DEFINE1,DEFINE2;DEFINE3
          // tested on "-define:DEF1;DEF2 -define:DEF3,DEF4;DEFFFF \n -define:DEF5"
          // result: DEF1, DEF2, DEF3, DEF4, DEFFFF, DEF5

          var definesList = new List<string>();
          var compileFlags = configText.Split(' ', '\n');
          foreach (var flag in compileFlags)
          {
            var f = flag.Trim();
            if (f.Contains(UNITY_DEFINE_KEYWORD))
            {
              var defineEndPos = f.IndexOf(UNITY_DEFINE_KEYWORD) + UNITY_DEFINE_KEYWORD.Length;
              var definesSubString = f.Substring(defineEndPos,f.Length - defineEndPos);
              definesSubString = definesSubString.Replace(";", ",");
              definesList.AddRange(definesSubString.Split(','));
            }
          }

          ApplyCustomDefines(definesList.ToArray(), projectContentElement, xmlns);
        }
      }
    }

    private static void ApplyCustomDefines(string[] customDefines, XElement projectContentElement, XNamespace xmlns)
    {
      var definesString = string.Join(";", customDefines);

      var defineConstants = projectContentElement
        .Elements(xmlns+"PropertyGroup")
        .Elements(xmlns+"DefineConstants")
        .FirstOrDefault(definesConsts=> !string.IsNullOrEmpty(definesConsts.Value));

      defineConstants?.SetValue(defineConstants.Value + ";" + definesString);
    }

    private static void ApplyAllowUnsafeBlocks(XElement projectContentElement, XNamespace xmlns)
    {
      projectContentElement.AddFirst(
        new XElement(xmlns + "PropertyGroup", new XElement(xmlns + "AllowUnsafeBlocks", true)));
    }

    private static bool IsPlayerProjectFile(string projectFile)
    {
      return Path.GetFileName(projectFile) == UNITY_PLAYER_PROJECT_NAME;
    }

    private static bool IsEditorProjectFile(string projectFile)
    {
      return Path.GetFileName(projectFile) == UNITY_EDITOR_PROJECT_NAME;
    }

    private static void SetXCodeDllReference(string name, XNamespace xmlns, XElement projectContentElement)
    {
      var unityAppBaseFolder = Path.GetDirectoryName(EditorApplication.applicationPath);

      var xcodeDllPath = Path.Combine(unityAppBaseFolder, Path.Combine("Data/PlaybackEngines/iOSSupport", name));
      if (!File.Exists(xcodeDllPath))
        xcodeDllPath = Path.Combine(unityAppBaseFolder, Path.Combine("PlaybackEngines/iOSSupport", name));

      if (File.Exists(xcodeDllPath))
      {
        var itemGroup = new XElement(xmlns + "ItemGroup");
        var reference = new XElement(xmlns + "Reference");
        reference.Add(new XAttribute("Include", Path.GetFileNameWithoutExtension(xcodeDllPath)));
        reference.Add(new XElement(xmlns + "HintPath", xcodeDllPath));
        itemGroup.Add(reference);
        projectContentElement.Add(itemGroup);
      }
    }

    private const string UNITY_REFERENCE_KEYWORD = "-r:";
    /// <summary>
    /// Handles custom references -r: in "mcs.rsp"
    /// </summary>
    /// <param name="projectContentElement"></param>
    /// <param name="xmlns"></param>
    private static void ApplyManualCompilingSettingsReferences(XElement projectContentElement, XNamespace xmlns)
    {
      if (!File.Exists(PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH))
        return;

      var configFilePath = PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH;

      if (File.Exists(configFilePath))
      {
        var configText = File.ReadAllText(configFilePath);
        if (configText.Contains(UNITY_REFERENCE_KEYWORD))
        {
          var referenceList = new List<string>();
          var compileFlags = configText.Split(' ', '\n');
          foreach (var flag in compileFlags)
          {
            var f = flag.Trim();
            if (f.Contains(UNITY_REFERENCE_KEYWORD))
            {
              var defineEndPos = f.IndexOf(UNITY_REFERENCE_KEYWORD) + UNITY_REFERENCE_KEYWORD.Length;
              var definesSubString = f.Substring(defineEndPos,f.Length - defineEndPos);
              definesSubString = definesSubString.Replace(";", ",");
              referenceList.AddRange(definesSubString.Split(','));
            }
          }

          foreach (var referenceName in referenceList)
          {
            string hintPath = null;
            if (PluginSettings.SystemInfoRiderPlugin.operatingSystemFamily == OperatingSystemFamilyRider.Windows)
            {
              var unityAppBaseFolder = Path.GetDirectoryName(EditorApplication.applicationPath);
              var monoDir = new DirectoryInfo(Path.Combine(unityAppBaseFolder, "MonoBleedingEdge/lib/mono"));
              if (!monoDir.Exists)
                monoDir = new DirectoryInfo(Path.Combine(unityAppBaseFolder, "Data/MonoBleedingEdge/lib/mono"));

              var newestApiDir = monoDir.GetDirectories("4.*").LastOrDefault();
              if (newestApiDir != null)
              {
                var dllPath = new FileInfo(Path.Combine(newestApiDir.FullName, referenceName));
                if (dllPath.Exists)
                  hintPath = dllPath.FullName;
              }
            }

            ApplyCustomReference(referenceName, projectContentElement, xmlns, hintPath);
          }
        }
      }
    }

    private static void ApplyCustomReference(string name, XElement projectContentElement, XNamespace xmlns, string hintPath = null)
    {
      var itemGroup = new XElement(xmlns + "ItemGroup");
      var reference = new XElement(xmlns + "Reference");
      reference.Add(new XAttribute("Include", Path.GetFileNameWithoutExtension(name)));
      if (!string.IsNullOrEmpty(hintPath))
        reference.Add(new XElement(xmlns + "HintPath", hintPath));
      itemGroup.Add(reference);
      projectContentElement.Add(itemGroup);
    }

    private static void FixTargetFrameworkVersion(XElement projectElement, XNamespace xmlns)
    {
      SetOrUpdateProperty(projectElement, xmlns, "TargetFrameworkVersion", existing =>
      {
        var expected = GetTargetFrameworkVersion();

        try
        {
          var current = existing.Length > 0 ? new Version(existing.Substring(1)) : new Version();
          return expected > current ? $"v{expected.ToString(2)}" : existing;
        }
        catch (Exception e)
        {
          ourLogger.Log(LoggingLevel.TRACE, $"Invalid existing TargetFrameworkVersion: {existing}", e);
        }

        return $"v{expected.ToString(2)}";
      });
    }

    private static Version GetTargetFrameworkVersion()
    {
      return UnityUtils.ScriptingRuntime > 0 ? GetNet40TargetFrameworkVersion() : GetNet20TargetFrameworkVersion();
    }

    private static Version GetNet40TargetFrameworkVersion()
    {
      if (PluginSettings.SystemInfoRiderPlugin.operatingSystemFamily == OperatingSystemFamilyRider.Windows &&
          !PluginSettings.OverrideTargetFrameworkVersion)
      {
        var versions = PluginSettings.GetInstalledNetFrameworks().Select(v => new Version(v)).ToList();
        versions.Sort();
        if (versions.Count > 0)
          return versions.Last();
      }

      return new Version(PluginSettings.TargetFrameworkVersion);
    }

    private static Version GetNet20TargetFrameworkVersion()
    {
      if (PluginSettings.SystemInfoRiderPlugin.operatingSystemFamily == OperatingSystemFamilyRider.Windows &&
          !PluginSettings.OverrideTargetFrameworkVersionOldMono)
      {
        var versions = PluginSettings.GetInstalledNetFrameworks().Select(v => new Version(v)).ToList();
        versions.Sort();
        if (versions.Count > 0)
          return versions.First();  // Don't know why this is first!?
      }

      return new Version(PluginSettings.TargetFrameworkVersionOldMono);
    }

    private static void SetLangVersion(XElement projectElement, XNamespace xmlns)
    {
      // Set the C# language level, so Rider doesn't have to guess (although it does a good job)
      // VSTU sets this, and I think newer versions of Unity do too (should check which version)
      SetOrUpdateProperty(projectElement, xmlns, "LangVersion", existing =>
      {
        var expected = GetExpectedLanguageLevel();
        if (expected == CSharpLangaugeLevel.Latest || existing == "latest")
          return "latest";

        // Only use our version if it's not already set, or it's less than what we would set
        // Note that if existing is "default", we'll override it
        if (!float.TryParse(existing, out var currentLanguageLevel)
          || (int)(currentLanguageLevel * 10) < (int)expected)
        {
          return $"{(int) expected / 10.0f:0.0}";
        }

        return existing;
      });
    }

    // ReSharper disable UnusedMember.Local
    private enum CSharpLangaugeLevel
    {
      CSharp20 = 20,
      CSharp30 = 30,
      CSharp40 = 40,
      CSharp50 = 50,
      CSharp60 = 60,
      CSharp70 = 70,
      CSharp71 = 71,
      CSharp72 = 72,
      Latest
    }
    // ReSharper restore UnusedMember.Local

    private static CSharpLangaugeLevel GetExpectedLanguageLevel()
    {
      // https://bitbucket.org/alexzzzz/unity-c-5.0-and-6.0-integration/src
      if (Directory.Exists(Path.GetFullPath("CSharp70Support")))
        return CSharpLangaugeLevel.Latest;  // "latest" - If we just return 7, that means 7.0. If we return 7.2, we might not have a 7.2 compiler
      if (Directory.Exists(Path.GetFullPath("CSharp60Support")))
        return CSharpLangaugeLevel.CSharp60;

      var apiCompatibilityLevel = 0;
      try
      {
        //PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup)
        var method = typeof(PlayerSettings).GetMethod("GetApiCompatibilityLevel");
        var parameter = typeof(EditorUserBuildSettings).GetProperty("selectedBuildTargetGroup");
        var val = parameter.GetValue(null, null);
        apiCompatibilityLevel = (int) method.Invoke(null, new [] {val});
      }
      catch (Exception ex)
      {
        ourLogger.Verbose("Exception on evaluating PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup)"+ ex);
      }

      try
      {
        var property = typeof(PlayerSettings).GetProperty("apiCompatibilityLevel");
        apiCompatibilityLevel = (int) property.GetValue(null, null);
      }
      catch (Exception)
      {
        ourLogger.Verbose("Exception on evaluating PlayerSettings.apiCompatibilityLevel");
      }

      // Unity 5.5+ supports C# 6, but only when targeting .NET 4.6. The enum doesn't exist pre Unity 5.5
      const int apiCompatibilityLevelNet46 = 3;
      if (apiCompatibilityLevel >= apiCompatibilityLevelNet46)
        return CSharpLangaugeLevel.CSharp60;

      return CSharpLangaugeLevel.CSharp40;
    }

    private static void SetProjectFlavour(XElement projectElement, XNamespace xmlns)
    {
      // This is the VSTU project flavour GUID, followed by the C# project type
      SetOrUpdateProperty(projectElement, xmlns, "ProjectTypeGuids",
        "{E097FAD1-6243-4DAD-9C02-E9B9EFC3FFC1};{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}");
    }

    private static void SetOrUpdateProperty(XElement root, XNamespace xmlns, string name, string content)
    {
      SetOrUpdateProperty(root, xmlns, name, v => content);
    }

    private static void SetOrUpdateProperty(XElement root, XNamespace xmlns, string name, Func<string, string> updater)
    {
      var element = root.Elements(xmlns + "PropertyGroup").Elements(xmlns + name).FirstOrDefault();
      if (element != null)
      {
        var result = updater(element.Value);
        if (result != element.Value)
        {
          if (ourLogger.IsVersboseEnabled())
            Debug.Log($"Overridding existing project property {name}. Old value: {element.Value}, new value: {result}");

          element.SetValue(result);
        }
      }
      else
        AddProperty(root, xmlns, name, updater(string.Empty));
    }

    // Adds a property to the first property group without a condition
    private static void AddProperty(XElement root, XNamespace xmlns, string name, object content)
    {
      if (ourLogger.IsVersboseEnabled())
        Debug.Log($"Adding project property {name}. Value: {content}");

      var propertyGroup = root.Elements(xmlns + "PropertyGroup")
        .FirstOrDefault(e => !e.Attributes(xmlns + "Condition").Any());
      if (propertyGroup == null)
      {
        propertyGroup = new XElement(xmlns + "PropertyGroup");
        root.AddFirst(propertyGroup);
      }

      propertyGroup.Add(new XElement(xmlns + name, content));
    }
  }
}