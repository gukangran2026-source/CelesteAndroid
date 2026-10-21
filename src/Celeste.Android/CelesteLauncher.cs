using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Android.Content;
using Android.Util;

namespace CelesteAndroid
{
	/// <summary>
	/// Carrega o Celeste.dll patcheado e chama o Main original, com o mesmo contrato do host desktop.
	/// </summary>
	public static class CelesteLauncher
	{
		public static void Run(Context context)
		{
			string gameDir = GameInstaller.GameDir(context);
			Log.Info(GameActivity.LogTag, $"Iniciando Celeste de {gameDir}");

			// Ver CelesteAndroid.HostConfig (Celeste.Android.Patches).
			// 存档根由 SaveLocation 决定：Download/Celeste 优先，内部目录兜底。
			// 迁移必须在 SetData 之前完成，否则首帧就会按旧路径建目录。
			SaveLocation.EnsureReady(context);

			AppContext.SetData("CelesteAndroid.Platform", "Android");
			AppContext.SetData("CelesteAndroid.PrefPath", SaveLocation.ResolvePrefPath(context));
			AppContext.SetData("CelesteAndroid.BackgroundPath", GameInstaller.BackgroundPng(context));

			// Everest 的 Mods 根和游戏数据根走同一个目录：Download/Celeste/Mods。
			// 存档已经是 Download/Celeste，Mods 放在它下面，用户一个地方管全部。
			AppContext.SetData("CelesteAndroid.Everest.Root", SaveLocation.ExternalRoot);
			AppContext.SetData("CelesteAndroid.CacheDir", context.CacheDir!.AbsolutePath);

			// O FNA resolve o Content relativo ao diretório de trabalho no Android.
			AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", gameDir + Path.DirectorySeparatorChar);
			Environment.CurrentDirectory = gameDir;

			Assembly celeste = AssemblyLoadContext.Default.LoadFromAssemblyPath(GameInstaller.PatchedDll(context));

			// Engine.AssemblyDirectory vem de Assembly.Location, que não aponta para o jogo.
			celeste.GetType("Monocle.Engine", throwOnError: true)!
				.GetField("AssemblyDirectory", BindingFlags.NonPublic | BindingFlags.Static)!
				.SetValue(null, gameDir);

			MethodInfo main = celeste.GetType("Celeste.Celeste", throwOnError: true)!
				.GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)!;
			main.Invoke(null, new object[] { Array.Empty<string>() });
		}
	}
}