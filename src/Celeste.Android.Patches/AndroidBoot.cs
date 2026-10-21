using System;
using System.IO;
using System.Reflection;
using MonoMod;

namespace CelesteAndroid.Everest
{
	/// <summary>
	/// 标记需要做 Android 启动适配的方法。
	///
	/// 这个属性必须定义在 MonoMod 命名空间以外的普通命名空间里，
	/// 因为 MonoMod 的 relinker 会把 mod 里所有类型按名字重新映射；
	/// 把运行时代码放进 MonoMod.* 命名空间会导致 relink 找不到目标
	/// （RelinkFailedException: failed relinking MonoMod.BootPatches）。
	/// </summary>
	[MonoModCustomMethodAttribute("ApplyAndroidBoot")]
	public class AndroidBootAttribute : Attribute
	{
	}

	/// <summary>被注入 IL 调用的运行时实现。</summary>
	public static class BootPatches
	{
		/// <summary>BOOT.Main 入口先跑：TMPDIR + Mods 目录 + PathGame。</summary>
		public static void BootInit()
		{
			HostConfig.SetupTempDir();
			string root = HostConfig.EnsureModsRoot();

			MethodInfo setter = HostConfig.ResolvePathGameSetter(typeof(BootPatches).Assembly);
			if (setter == null)
			{
				Console.WriteLine(HostConfig.LogTag + " Everest.PathGame 未找到，Mods 用默认路径");
				return;
			}
			setter.Invoke(null, new object[] { root });
			Console.WriteLine(HostConfig.LogTag + " PathGame -> " + root);
		}
	}
}