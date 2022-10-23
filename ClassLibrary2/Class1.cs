using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

using UnityEngine;
using HarmonyLib;

using BrilliantSkies.Modding;
using BrilliantSkies.Core.Logger;
using BrilliantSkies.Core.Constants;
using Newtonsoft.Json;

namespace pudy248.CoreLib
{
	public class Plugin : GamePlugin_PostLoad
	{
		public string name
		{
			get { return "pudy248_CoreLib"; }
		}
		public Version version
		{
			get { return new Version(1, 1, 4, 0); }
		}
		public void OnLoad()
		{
			AdvLogger.LogInfo("pudy248's CoreLib v1.1.4 plugin loaded");
		}

		public bool AfterAllPluginsLoaded()
		{
			var harmony = new Harmony("com.pudy248.CoreLib");
			if (CallRedirector.GetRedirectCount() > 0) harmony.PatchAll();
			AdvLogger.LogInfo("pudy248's CoreLib: All patches applied");
			return true;
		}

		public void OnSave() { }
	}

	[HarmonyPatch]
	public static class CallRedirector
	{
		class CallRedirect
		{
			public MethodInfo orig;
			public MethodInfo redirect;
			public bool found;

			public CallRedirect(MethodInfo orig, MethodInfo redirect)
			{
				this.orig = orig;
				this.redirect = redirect;
				this.found = false;
			}
		}
		static ConcurrentDictionary<MethodBase, ConcurrentBag<CallRedirect>> singleRedirects = new ConcurrentDictionary<MethodBase, ConcurrentBag<CallRedirect>>();
		static ConcurrentDictionary<MethodBase, MethodInfo> globalRedirects = new ConcurrentDictionary<MethodBase, MethodInfo>();
		public static int GetRedirectCount() => singleRedirects.Count + globalRedirects.Count;
		public static void SingleRedirect(MethodBase scope, MethodInfo orig, MethodInfo redirect)
		{
			Type[] origTypes = !orig.IsStatic ? new Type[] {orig.DeclaringType}.AddRangeToArray(orig.GetParameterTypes()) : orig.GetParameterTypes();
			Type[] redirectTypes = !redirect.IsStatic ? new Type[] { redirect.DeclaringType }.AddRangeToArray(redirect.GetParameterTypes()) : redirect.GetParameterTypes();
			if(!Enumerable.SequenceEqual<Type>(origTypes, redirectTypes))
			{
				throw new ArgumentException("Parameter types don't match!");
			}
			if(orig.ReturnType != redirect.ReturnType)
			{
				throw new ArgumentException("Return types don't match!");
			}
			if(!singleRedirects.ContainsKey(scope))
			{
				singleRedirects.TryAdd(scope, new ConcurrentBag<CallRedirect>());
				singleRedirects[scope].Add(new CallRedirect(orig, redirect));
			}
			else
				singleRedirects[scope].Add(new CallRedirect(orig, redirect));
		}
		public static void GlobalRedirect(MethodInfo orig, MethodInfo redirect)
		{
			Type[] origTypes = !orig.IsStatic ? new Type[] { orig.DeclaringType }.AddRangeToArray(orig.GetParameterTypes()) : orig.GetParameterTypes();
			Type[] redirectTypes = !redirect.IsStatic ? new Type[] { redirect.DeclaringType }.AddRangeToArray(redirect.GetParameterTypes()) : redirect.GetParameterTypes();
			if (!Enumerable.SequenceEqual<Type>(origTypes, redirectTypes))
			{
				throw new ArgumentException("Parameter types don't match!");
			}
			if (orig.ReturnType != redirect.ReturnType)
			{
				throw new ArgumentException("Return types don't match!");
			}
			if (!globalRedirects.ContainsKey(orig))
			{
				globalRedirects.TryAdd(orig, redirect);
			}
			else throw new ArgumentException("Method already overridden!");
		}
		[HarmonyTargetMethods]
		static IEnumerable<MethodBase> AllTargets()
		{
			foreach (MethodBase mb in singleRedirects.Keys) yield return mb;
			foreach (MethodBase mb in globalRedirects.Keys) yield return mb;
		}

		[HarmonyTranspiler]
		static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase scope)
		{
			if (singleRedirects.ContainsKey(scope)) {
				foreach (CodeInstruction instruction in instructions)
				{
					if (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
					{
						bool localFound = false;
						foreach (CallRedirect redirect in singleRedirects[scope])
						{
							if ((MethodInfo)instruction.operand == redirect.orig)
							{
								yield return new CodeInstruction(instruction.opcode, redirect.redirect);
								redirect.found = true;
								localFound = true;
								break;
							}
						}
						if (!localFound)
						{
							yield return instruction;
						}
					}
					else yield return instruction;
				}
				foreach (CallRedirect redirect in singleRedirects[scope])
				{
					if (!redirect.found)
						AdvLogger.LogWarning("No matching method for " + redirect.orig.ToString() + " found in " + scope.ToString(), LogOptions._AlertDevInGame);
				}
			}
			else if (globalRedirects.ContainsKey(scope)) 
			{
				int paramCount = (!scope.IsStatic ? new Type[] { scope.DeclaringType }.AddRangeToArray(scope.GetParameterTypes()) : scope.GetParameterTypes()).Length;
				if (paramCount > 0) yield return new CodeInstruction(OpCodes.Ldarg_0);
				if (paramCount > 1) yield return new CodeInstruction(OpCodes.Ldarg_1);
				if (paramCount > 2) yield return new CodeInstruction(OpCodes.Ldarg_2);
				if (paramCount > 3) yield return new CodeInstruction(OpCodes.Ldarg_3);
				if (paramCount > 4)
				{
					for(int i = 4; i < paramCount; i++)
					{
						yield return new CodeInstruction(OpCodes.Ldarg, i);
					}
				}
				yield return new CodeInstruction(OpCodes.Call, globalRedirects[scope]);
				yield return new CodeInstruction(OpCodes.Ret);
			}
			else foreach (CodeInstruction instruction in instructions) yield return instruction;
		}
	}

	
	public abstract class ConfigEntryBase
	{
		public string name { get; protected set; }
		public string desc { get; protected set; }
		public string type { get; protected set; }
		public object defaultValue { get; protected set; }
		public abstract object value { get; set; }

		public ConfigEntry<T> ToGeneric<T>()
		{
			return new ConfigEntry<T>(name, desc, type, defaultValue, value);
		}
	}
	public class ConfigEntry<T> : ConfigEntryBase
	{
		T field;
		public override object value
		{
			get
			{
				return field;
			}
			set
			{
				if (!AvailableCastChecker.CanCast(value.GetType(), Type.GetType(type))) throw new ArgumentException("Set value is not of the correct type!");
				this.field = (T)(value);
			}
		}
		public static implicit operator T(ConfigEntry<T> cfg) => cfg.field;

		public ConfigEntry(string name, string desc, string type, object defaultValue, object boxedField)
		{
			this.name = name;
			this.desc = desc;
			this.type = type;
			this.defaultValue = defaultValue;
			this.value = boxedField;
		}
	}
	public class Config
	{
		public string guid;
		public string path;
		public IReadOnlyDictionary<string, ConfigEntryBase> entries { get; private set; }
		public object this[string key]
		{
			get
			{
				if(!entries.TryGetValue(key, out ConfigEntryBase entry)) throw new KeyNotFoundException(key);
				return entry.value;
			}
			set
			{
				if (!entries.TryGetValue(key, out ConfigEntryBase entry)) throw new KeyNotFoundException(key);
				if (!AvailableCastChecker.CanCast(value.GetType(), Type.GetType(entry.type))) throw new ArgumentException("Set value is not of the correct type!");
				Type t = Type.GetType(entry.type);
				entry.value = value;
			}
		}

		public void Reload()
		{
			Dictionary<string, ConfigEntryBase> temp = new Dictionary<string, ConfigEntryBase>();
			string[] lines = File.ReadAllLines(ConfigHelper.cfgPath() + path);
			string name = "";
			string desc = "";
			string type = "";
			object defaultValue = null;
			object value = null;
			foreach (string line in lines)
			{
				if (line.Length < 2) continue;
				if (line.StartsWith("### ")) guid = line.Substring(4);
				if (line.StartsWith("## ")) desc = line.Substring(3);
				else if (line.StartsWith("# Type: ")) type = line.Substring(8);
				else if (line.StartsWith("# Default: "))
				{
					if (type == "") throw new Exception("Invalid config!");
					defaultValue = JsonConvert.DeserializeObject(line.Substring(11), Type.GetType(type));
				}
				else if (line.Contains("="))
				{
					name = line.Substring(0, line.IndexOf("=") - 1);
					if (type == "") throw new Exception("Invalid config!");
					value = JsonConvert.DeserializeObject(line.Substring(line.IndexOf("=") + 1), Type.GetType(type));

					//Reflection pain
					Type generic = typeof(ConfigEntry<>).MakeGenericType(new Type[] { Type.GetType(type) });
					ConstructorInfo constructor = generic.GetConstructor(new Type[] { typeof(string), typeof(string), typeof(string), typeof(object), typeof(object) });
					temp.Add(name, (ConfigEntryBase)constructor.Invoke(new object[] { name, desc, type, defaultValue, value }));

					name = "";
					desc = "";
					type = "";
					defaultValue = null;
					value = null;
				}
			}
			this.entries = temp;
		}

		public void Save()
		{
			StreamWriter sw = File.CreateText(ConfigHelper.cfgPath() + path);
			sw.WriteLine("### " + guid);

			foreach (ConfigEntryBase entry in entries.Values)
			{
				sw.WriteLine("");
				if (entry.desc != null) sw.WriteLine("## " + entry.desc);
				sw.WriteLine("# Type: " + entry.type);
				if (entry.defaultValue != null) sw.WriteLine("# Default: " + JsonConvert.SerializeObject(entry.defaultValue));
				sw.WriteLine(entry.name + " = " + JsonConvert.SerializeObject(entry.value));
			}

			sw.Close();
		}

		public Config(string guid, string path, List<ConfigEntryBase> entries)
		{
			this.guid = guid;
			this.path = path;
			Dictionary<string, ConfigEntryBase> temp = new Dictionary<string, ConfigEntryBase>();
			foreach(ConfigEntryBase entry in entries) temp.Add(entry.name, entry);
			this.entries = temp;
		}

		public ConfigDefinition ToDefinition()
		{
			ConfigDefinition temp = new ConfigDefinition(guid, path);
			foreach(ConfigEntryBase entry in entries.Values)
			{
				temp.AddEntry(entry.name, entry.desc, entry.type, entry.defaultValue);
			}
			return temp;
		}
	}
	
	public class ConfigEntryDefinition
	{
		public string name;
		public string desc;
		public string type;
		public object defaultValue;
		public ConfigEntryDefinition(string name, string desc, string type, object defaultValue)
		{
			this.name = name;
			this.desc = desc;
			if (!AvailableCastChecker.CanCast(defaultValue.GetType(), Type.GetType(type))) throw new ArgumentException("Default value is not of the correct type!");
			this.type = type;
			this.defaultValue = defaultValue;
		}
	}
	public class ConfigDefinition
	{
		public string guid;
		public string path;
		public List<ConfigEntryDefinition> entries = new List<ConfigEntryDefinition>();
		public ConfigDefinition(string guid, string path)
		{
			this.guid = guid;
			this.path = path;
		}
		public void AddEntry(string name, string desc, string type, object defaultValue)
		{
			entries.Add(new ConfigEntryDefinition(name, desc, type, defaultValue));
		}
	}

	public static class ConfigHelper
	{
		static object lockObj = new object();
		public static string cfgPath()
		{
			string path = Get.PermanentPaths.RootDir().Append("Config/").ToString();
			Directory.CreateDirectory(path);
			return path;
		}
		public static Config LoadFile(string path)
		{
			if(path.Contains("/"))
			{
				string folderDir = cfgPath();
				string[] folders = path.Split('/');
				for(int i = 0; i < folders.Length - 1; i++)
				{
					folderDir = folderDir + folders[i] + "/";
					if(!Directory.Exists(folderDir)) Directory.CreateDirectory(folderDir);
				}
			}
			if (File.Exists(cfgPath() + path))
			{
				lock (lockObj)
				{
					string[] lines = File.ReadAllLines(cfgPath() + path);
					List<ConfigEntryBase> cfgEntries = new List<ConfigEntryBase>();
					string guid = "";
					string name = "";
					string desc = "";
					string type = "";
					object defaultValue = null;
					object value = null;
					foreach (string line in lines)
					{
						if (line.Length < 2) continue;
						if (line.StartsWith("### ")) guid = line.Substring(4);
						if (line.StartsWith("## ")) desc = line.Substring(3);
						else if (line.StartsWith("# Type: ")) type = line.Substring(8);
						else if (line.StartsWith("# Default: "))
						{
							if (type == "") throw new Exception("Invalid config!");
							defaultValue = JsonConvert.DeserializeObject(line.Substring(11), Type.GetType(type));
						}
						else if (line.Contains("="))
						{
							name = line.Substring(0, line.IndexOf("=") - 1);
							if (type == "") throw new Exception("Invalid config!");
							value = JsonConvert.DeserializeObject(line.Substring(line.IndexOf("=") + 1), Type.GetType(type));

							//Reflection pain
							Type generic = typeof(ConfigEntry<>).MakeGenericType(new Type[] { Type.GetType(type) });
							ConstructorInfo constructor = generic.GetConstructor(new Type[] { typeof(string), typeof(string), typeof(string), typeof(object), typeof(object) });
							cfgEntries.Add((ConfigEntryBase)constructor.Invoke(new object[] { name, desc, type, defaultValue, value }));

							name = "";
							desc = "";
							type = "";
							defaultValue = null;
							value = null;
						}
					}
					return new Config(guid, path, cfgEntries);
				}
			}
			else return null;
		}

		//Returns true if file was created or overwritten
		public static bool CreateFile(ConfigDefinition definition, bool overwrite = false)
		{
			if (definition.path.Contains("/"))
			{
				string folderDir = cfgPath();
				string[] folders = definition.path.Split('/');
				for (int i = 0; i < folders.Length - 1; i++)
				{
					folderDir = folderDir + folders[i] + "/";
					if (!Directory.Exists(folderDir)) Directory.CreateDirectory(folderDir);
				}
			}
			if (!File.Exists(cfgPath() + definition.path) || overwrite)
			{
				lock (lockObj)
				{
					StreamWriter sw = File.CreateText(cfgPath() + definition.path);
					sw.WriteLine("### " + definition.guid);

					foreach (ConfigEntryDefinition entry in definition.entries)
					{
						sw.WriteLine("");
						if (entry.desc != "") sw.WriteLine("## " + entry.desc);
						sw.WriteLine("# Type: " + entry.type);
						if (entry.defaultValue != null) sw.WriteLine("# Default: " + JsonConvert.SerializeObject(entry.defaultValue));
						sw.WriteLine(entry.name + " = " + JsonConvert.SerializeObject(entry.defaultValue));
					}

					sw.Close();
					return true;
				}
			}
			return false;
		}
	
		public static Config AppendToExisting(Config original, ConfigDefinition newDefinition)
		{
			lock (lockObj)
			{
				StreamWriter sw = File.CreateText(cfgPath() + newDefinition.path);
				sw.WriteLine("### " + newDefinition.guid);

				foreach (ConfigEntryDefinition entry in newDefinition.entries)
				{
					sw.WriteLine("");
					if (entry.desc != "") sw.WriteLine("## " + entry.desc);
					sw.WriteLine("# Type: " + entry.type);
					if (entry.defaultValue != null) sw.WriteLine("# Default: " + JsonConvert.SerializeObject(entry.defaultValue));
					if (original.entries.ContainsKey(entry.name))
						sw.WriteLine(entry.name + " = " + JsonConvert.SerializeObject(original[entry.name]));
					else
						sw.WriteLine(entry.name + " = " + JsonConvert.SerializeObject(entry.defaultValue));
				}

				sw.Close();
			}
			
			return LoadFile(newDefinition.path);
		}
	}
	//Thank you StackOverflow! https://stackoverflow.com/a/32026590
	class AvailableCastChecker
	{
		public static bool CanCast(Type from, Type to)
		{
			if (from.IsAssignableFrom(to))
			{
				return true;
			}
			if (HasImplicitConversion(from, from, to) || HasImplicitConversion(to, from, to))
			{
				return true;
			}
			List<Type> list;
			if (ImplicitNumericConversions.TryGetValue(from, out list))
			{
				if (list.Contains(to))
					return true;
			}

			if (to.IsEnum)
			{
				return CanCast(from, Enum.GetUnderlyingType(to));
			}
			if (Nullable.GetUnderlyingType(to) != null)
			{
				return CanCast(from, Nullable.GetUnderlyingType(to));
			}

			return false;
		}

		// https://msdn.microsoft.com/en-us/library/y5b434w4.aspx
		static Dictionary<Type, List<Type>> ImplicitNumericConversions = new Dictionary<Type, List<Type>>();

		static AvailableCastChecker()
		{
			ImplicitNumericConversions.Add(typeof(sbyte), new List<Type> { typeof(short), typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal) });
			ImplicitNumericConversions.Add(typeof(byte), new List<Type> { typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal) });
			ImplicitNumericConversions.Add(typeof(short), new List<Type> { typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal) });
			ImplicitNumericConversions.Add(typeof(ushort), new List<Type> { typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal) });
			ImplicitNumericConversions.Add(typeof(int), new List<Type> { typeof(long), typeof(float), typeof(double), typeof(decimal) });
			ImplicitNumericConversions.Add(typeof(uint), new List<Type> { typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal) });
			ImplicitNumericConversions.Add(typeof(long), new List<Type> { typeof(float), typeof(double), typeof(decimal) });
			ImplicitNumericConversions.Add(typeof(char), new List<Type> { typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal) });
			ImplicitNumericConversions.Add(typeof(float), new List<Type> { typeof(double) });
			ImplicitNumericConversions.Add(typeof(ulong), new List<Type> { typeof(float), typeof(double), typeof(decimal) });
		}

		static bool HasImplicitConversion(Type definedOn, Type baseType, Type targetType)
		{
			return definedOn.GetMethods(BindingFlags.Public | BindingFlags.Static)
				.Where(mi => mi.Name == "op_Implicit" && mi.ReturnType == targetType)
				.Any(mi =>
				{
					ParameterInfo pi = mi.GetParameters().FirstOrDefault();
					return pi != null && pi.ParameterType == baseType;
				});

		}
	}

	public class PatchMeta
	{
		public string[] depends;
		public string filename;
	}

	/*public class PluginManager
	{
		public IReadOnlyList<PluginMeta> LoadedPlugins { get => _loadedPlugins; }
		private List<PluginMeta> _loadedPlugins = new List<PluginMeta>();

		public static void LoadPlugins(PluginLoader instance)
		{
			AdvLogger.LogInfo(string.Concat(new string[]
				{
					"Plugin loader initializing. ",
					Get.Game.VersionString,
					" - ",
					DateTime.Now.ToString("G"),
					"..."
			}), LogOptions.None);

			instance.ImportCurrentAssemblies();
			List<PluginMeta> allPlugins = new List<PluginMeta>();
			List<PatchMeta> allPatches = new List<PatchMeta>();
			instance.FindPlugins(Get.PermanentPaths.RootModDir().ToString(), allPlugins);
			instance.FindPlugins(Get.PermanentPaths.CoreModDir().ToString(), allPlugins);
			List<PluginMeta> validPlugins = new List<PluginMeta>();
			List<PatchMeta> validPatches = new List<PatchMeta>();
			instance.CheckDependencies(allPlugins, validPlugins);
			bool flag = validPlugins.Count > 0;
			if (flag)
			{
				LoadAssemblies(instance, validPlugins);
				instance.LoadAllValidPlugins(validPlugins);
				instance.AfterLoadAllValidPlugins(validPlugins);
			}
			instance.m_AreAllPluginsLoaded = true;
			AdvLogger.LogInfo("Done with plugins.", LogOptions.None);
		}

		public static bool FullCheck(string moddir, out PluginMeta meta)
		{
			meta = null;
			string text = Path.Combine(moddir, "plugin.json");
			string fileName = Path.GetFileName(moddir.TrimEnd(new char[]
			{
				'\\',
				'/'
			}));
			bool result;
			if (!File.Exists(text))
			{
				if (MetaFileChecker.IntenseLogging)
				{
					AdvLogger.LogInfo(moddir + " is missing plugin.json.", LogOptions.None);
				}
				result = false;
			}
			else
			{
				try
				{
					bool invalidJson = !MetaFileChecker.AssertKeysExist(text);
					if (invalidJson)
					{
						return false;
					}
				}
				catch (Exception e)
				{
					ModProblems.AddModProblem(fileName, moddir, MetaFileChecker._locFile.Get("Error_FieldsIncorrectSyntax", "Fatal error loading checking fields of 'plugin.json' file. The JSON syntax must be incorrect.", true), false);
					AdvLogger.LogException("Exception while parsing json file '" + text, e, LogOptions._AlertDevInGameOnHudOnly);
					return false;
				}
				try
				{
					meta = JsonConvert.DeserializeObject<PluginMeta>(File.ReadAllText(text));
					meta.TidyUp(moddir);
				}
				catch (Exception e2)
				{
					ModProblems.AddModProblem(fileName, moddir, MetaFileChecker._locFile.Get("Error_IncorrectSyntax", "Fatal error loading 'plugin.json' file. The JSON syntax must be incorrect.", true), false);
					AdvLogger.LogException("Exception while parsing json file '" + text + "'", e2, LogOptions._AlertDevInGameOnHudOnly);
					return false;
				}
				bool pluginLoaded = false;
				bool exactVersionMatch = meta.gameversion == Get.Game.VersionString;
				if (exactVersionMatch)
				{
					pluginLoaded = true;
				}
				else
				{
					Version currentVer = Get.Game.GetAsVersionObject();
					Version lastCompatibleVer = Get.Game.GetAsLastCompatibleVersionObject();
					Version pluginVer = VersionConversion.ConvertToVersion(meta.gameversion);
					bool pluginVerDisabled = pluginVer == new Version(6, 6, 6, 0);
					if (pluginVerDisabled)
					{
						pluginLoaded = true;
					}
					else
					{
						bool oldVer = pluginVer < lastCompatibleVer;
						if (oldVer)
						{
							AdvLogger.LogWarning(string.Concat(new string[]
							{
								"'",
								moddir,
								"' is obsolete, it is designed to work with game version '",
								meta.gameversion,
								"' and may not work with game version '",
								Get.Game.VersionString,
								"'"
							}), LogOptions._AlertDevInGameOnHudOnly);
							ModProblems.AddModProblem(meta, MetaFileChecker._locFile.Format("Error_Obsolete", "Obsolete ('{0}')", new object[]
							{
								meta.gameversion
							}), true);
						}
						else
						{
							bool newVer = pluginVer > currentVer;
							if (newVer)
							{
								AdvLogger.LogWarning(string.Concat(new string[]
								{
									"'",
									moddir,
									"' is too recent, it is designed to work with game version '",
									meta.gameversion,
									"' and may not work with game version '",
									Get.Game.VersionString,
									"'"
								}), LogOptions.None);
								ModProblems.AddModProblem(meta, MetaFileChecker._locFile.Format("Error_TooRecent", "Too recent ('{0}')", new object[]
								{
									meta.gameversion
								}), true);
							}
							else
							{
								pluginLoaded = true;
							}
						}
					}
				}
				result = pluginLoaded;
			}
			return result;
		}
		public static void FindPlugins(PluginLoader instance, string rootDir, List<PluginMeta> metaList, List<PatchMeta> patchList)
		{
			bool flag = Directory.Exists(rootDir);
			if (flag)
			{
				AdvLogger.LogInfo("Checking " + rootDir + " for plugins...", LogOptions.None);
				foreach (string text in Directory.GetDirectories(rootDir))
				{
					Guid guidOfMod;
					bool flag2 = !ModDisabler.CheckModEnabled(text, out guidOfMod);
					if (flag2)
					{
						AdvLogger.LogInfo("Mod in " + text + " is disabled, skipping plugin load.", LogOptions.None);
					}
					else
					{
						PluginMeta pluginMeta;
						bool flag3 = MetaFileChecker.FullCheck(text, out pluginMeta);
						if (flag3)
						{
							pluginMeta.GuidOfMod = guidOfMod;
							metaList.Add(pluginMeta);
						}
					}
				}
			}
			else
			{
				AdvLogger.LogInfo(rootDir + " did not exist and as such contains no plugins plugins...", LogOptions.None);
			}
		}
		public static void LoadAssemblies(PluginLoader instance, List<PluginMeta> validPlugins)
		{
			int num = validPlugins.Count;
			for (int i = 0; i < num; i++)
			{
				PluginMeta pluginMeta = validPlugins[i];
				string[] array = pluginMeta.filename.Split(new char[]
				{
					','
				}, StringSplitOptions.RemoveEmptyEntries);
				string text = array.LastOrDefault<string>();
				try
				{
					for (int j = 0; j < array.Length - 1; j++)
					{
						try
						{
							Assembly assembly = Assembly.LoadFile(Path.Combine(pluginMeta.dir, array[j]));
						}
						catch (Exception ex)
						{
							ModProblem modProblem = ModProblems.AddModProblem(pluginMeta, "DLL " + array[j] + " threw error when loading", true);
							modProblem.Guid = pluginMeta.GuidOfMod;
						}
					}
					string fullPath = Path.GetFullPath(Path.Combine(pluginMeta.dir, text));
					Assembly assembly2 = Assembly.LoadFile(fullPath);
					bool flag = instance.Classes.PreloadTypes(assembly2);
					bool flag2 = !flag;
					if (flag2)
					{
						foreach (Type type in assembly2.GetTypes())
						{
							bool flag3 = typeof(GamePlugin).IsAssignableFrom(type);
							if (flag3)
							{
								GamePlugin gamePlugin = (GamePlugin)type.GetConstructor(new Type[0]).Invoke(null);
								bool flag4 = gamePlugin != null;
								if (flag4)
								{
									pluginMeta.CustomPlugins.Add(gamePlugin);
									AdvLogger.LogInfo("Plugin '" + gamePlugin.name + "' loaded", LogOptions.None);
								}
							}
						}
					}
					else
					{
						ModProblem modProblem2 = ModProblems.AddModProblem(pluginMeta, PluginLoader._locFile.Get("Error_FatalError", "Fatal error while loading mod. Please delete mod until it has been updated to ensure normal play (or disable mod loading).", true), true);
						modProblem2.Guid = pluginMeta.GuidOfMod;
						validPlugins.Remove(pluginMeta);
						i--;
						num--;
					}
				}
				catch (FileNotFoundException e)
				{
					AdvLogger.LogException("Plugin DLL not found while loading plugin '" + text + "'", e, LogOptions.None);
					ModProblem modProblem3 = ModProblems.AddModProblem(pluginMeta, PluginLoader._locFile.Get("Error_NoDLL", "Fatal error while loading mod (DLL not found). Please delete mod until it has been updated to ensure normal play (or disable mod loading).", true), true);
					modProblem3.Guid = pluginMeta.GuidOfMod;
					validPlugins.Remove(pluginMeta);
					i--;
					num--;
				}
				catch (Exception e2)
				{
					AdvLogger.LogException("Exception while loading plugin '" + text + "'", e2, LogOptions.FailTesting);
					ModProblem modProblem4 = ModProblems.AddModProblem(pluginMeta, PluginLoader._locFile.Get("Error_FatalError2", "Fatal error while loading mod. Please delete mod until it has been updated to ensure normal play (or disable mod loading).", true), true);
					modProblem4.Guid = pluginMeta.GuidOfMod;
					validPlugins.Remove(pluginMeta);
					i--;
					num--;
				}
			}
		}
	}

	public interface PluginPatch : GamePlugin
	{

	}

	public interface PluginPatch_PostLoad : GamePlugin_PostLoad
	{

	}*/
}