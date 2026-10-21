using System;
using System.IO;
using System.Reflection;
using MonoMod;

namespace CelesteAndroid.Everest
{
	/// <summary>
	/// Everest 在 Android 上的宿主配置。
	///
	/// 目标布局（和存档同一个根）：
	///   /sdcard/Download/Celeste/
	///   ├── Mods/                     ← 模组放这里
	///   │   └── &lt;mod名&gt;/
	///   │       ├── everest.yaml
	///   │       └── &lt;mod名&gt;.dll
	///   ├── Saves/                    ← 存档
	///   ├── Backups/                  ← 游戏内备份
	///   ├── settings.celeste
	///   └── modsettings-Everest       ← Everest 自己的设置
	///
	/// 与桌面版的差异：
	/// 1. Mods 根：桌面是 PathGame/Mods，这里固定到存档根下的 Mods。
	/// 2. TMPDIR：MonoMod.Core 要把内嵌 exhelper_linux_arm64.so 解压到临时目录再 dlopen，
	///    Android 没有可写的 /tmp。
	/// 3. SELinux execheap 探测：BOOT.Main 在 Linux 平台会 mprotect(PROT_EXEC) 探测，
	///    Android 上必然失败并抛异常，必须跳过。
	/// 4. 进程重启：Everest 切换 vanilla / 重载 mod 会 Process.Start 自己，
	///    Android 上不存在可执行入口。
	///
	/// 注意：Celeste.Mod.* 由 Everest 补丁在合并阶段注入，
	/// 编译本模块时 Celeste.exe 里还没有它们，所以一律走反射。
	/// </summary>
	public static class HostConfig
	{
		public const string RootKey = "CelesteAndroid.Everest.Root";
		public const string EnabledKey = "CelesteAndroid.Everest.Enabled";
		public const string CacheDirKey = "CelesteAndroid.CacheDir";
		public const string LogTag = "[EverestAndroid]";

		/// <summary>
		/// 游戏数据根。优先用 CelesteLauncher 传进来的 SaveLocation.ExternalRoot，
		/// 没传就退回 /sdcard/Download/Celeste。
		/// </summary>
		public static string Root
		{
			get
			{
				string r = AppContext.GetData(RootKey) as string;
				if (!string.IsNullOrEmpty(r))
					return r;
				return Path.Combine(DownloadDir, "Celeste");
			}
		}

		private static string DownloadDir
		{
			get
			{
				string pref = AppContext.GetData("CelesteAndroid.PrefPath") as string;
				if (!string.IsNullOrEmpty(pref))
					return pref;
				return "/sdcard/Download";
			}
		}

		/// <summary>Mods 目录：&lt;Root&gt;/Mods</summary>
		public static string ModsRoot => Path.Combine(Root, "Mods");

		public static string CacheDir
		{
			get
			{
				string c = AppContext.GetData(CacheDirKey) as string;
				if (!string.IsNullOrEmpty(c))
					return c;
				return Path.Combine(Path.GetTempPath(), "celeste");
			}
		}

		public static bool Enabled =>
			(AppContext.GetData(EnabledKey) as string) != "0";

		/// <summary>确保根目录和 Mods 目录存在，写一份说明文件。</summary>
		public static string EnsureModsRoot()
		{
			string root = ModsRoot;
			try
			{
				Directory.CreateDirectory(root);

				string readme = Path.Combine(root, "怎么加模组.txt");
				if (!File.Exists(readme))
				{
					File.WriteAllText(readme,
						"加模组的方法\n" +
						"\n" +
						"一、把模组文件夹整个放进这个目录，每个模组一个子目录：\n" +
						"\n" +
						"    " + root + "/模组名/everest.yaml\n" +
						"    " + root + "/模组名/模组名.dll\n" +
						"    " + root + "/模组名/Dialog/...\n" +
						"    " + root + "/模组名/Graphics/...\n" +
						"\n" +
						"二、也可以直接把模组压缩包 .zip 放进这个目录，游戏会自己读。\n" +
						"\n" +
						"三、放好之后重启游戏，主菜单进 MODS，勾选启用，再回主菜单进 PLAY。\n" +
						"\n" +
						"四、想临时不加载任何模组，在 " + Path.GetDirectoryName(root) + " 下新建空文件：\n" +
						"    nextLaunchIsVanilla.txt\n" +
						"    游戏读一次就自动删掉，那一次启动就是纯原版。\n" +
						"\n" +
						"删模组就是删对应文件夹，不需要重装游戏。\n");
				}

				string vanillaFlag = Path.Combine(Path.GetDirectoryName(root) ?? root, "nextLaunchIsVanilla.txt");
				if (File.Exists(vanillaFlag))
					Console.WriteLine(LogTag + " 检测到 nextLaunchIsVanilla.txt，本次以原版启动");
			}
			catch (Exception e)
			{
				Console.WriteLine(LogTag + " Mods 目录创建失败: " + e.Message);
			}
			return root;
		}

		/// <summary>把 TMPDIR 指到应用可写目录，MonoMod.Core 靠它解压 exhelper。</summary>
		public static void SetupTempDir()
		{
			try
			{
				string cache = CacheDir;
				Directory.CreateDirectory(cache);
				Environment.SetEnvironmentVariable("TMPDIR", cache);
				Environment.SetEnvironmentVariable("TMP", cache);
				Environment.SetEnvironmentVariable("TEMP", cache);
				Console.WriteLine(LogTag + " TMPDIR -> " + cache);
			}
			catch (Exception e)
			{
				Console.WriteLine(LogTag + " 设置 TMPDIR 失败: " + e.Message);
			}
		}

		public static bool IsWritable(string dir)
		{
			if (string.IsNullOrEmpty(dir))
				return false;
			try
			{
				Directory.CreateDirectory(dir);
				string probe = Path.Combine(dir, ".ev-write-probe");
				File.WriteAllText(probe, "1");
				File.Delete(probe);
				return true;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>反射拿 Everest.PathGame 的 setter，避免编译期依赖 Celeste.Mod。</summary>
		public static MethodInfo ResolvePathGameSetter(Assembly celeste)
		{
			Type t = celeste.GetType("Celeste.Mod.Everest", throwOnError: false);
			if (t == null)
				return null;
			PropertyInfo p = t.GetProperty("PathGame", BindingFlags.Public | BindingFlags.Static);
			return p == null ? null : p.GetSetMethod();
		}
	}
}