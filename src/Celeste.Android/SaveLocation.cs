using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Util;
using AndroidUri = Android.Net.Uri;

namespace CelesteAndroid
{
	public enum SaveMode
	{
		/// <summary>Download/Celeste：游戏原生读写，需要「所有文件访问」。</summary>
		External,
		/// <summary>files/userdata/Celeste：兜底，退出时镜像到 Download。</summary>
		Internal
	}

	/// <summary>
	/// 存档根管理。
	///
	/// 目标结构（外部模式）：
	///   /sdcard/Download/Celeste/Saves/0.celeste ...
	///   /sdcard/Download/Celeste/settings.celeste
	///   /sdcard/Download/Celeste/Backups/*.celeste
	///
	/// 不变量：
	/// 1. 旧位置 files/userdata/Celeste 只读，任何路径都不删除它。
	/// 2. 迁移 = 复制 + 逐文件 SHA256 校验，失败整批回滚。
	/// 3. 迁移成功后再打一份 zip 快照到 Download/Celeste。
	/// 4. 每次暂停双向同步，Download 与内部影子各留一份。
	/// 5. 幂等判定看文件系统（目标 Saves 里有没有 .celeste），不看 SharedPreferences。
	/// </summary>
	public static class SaveLocation
	{
		private const string Tag = "CelesteAndroid";
		public const string GameFolder = "Celeste";
		public const string SavesFolder = "Saves";
		public const string SettingsFile = "settings.celeste";
		public const string BackupsFolder = "Backups";
		private const string ProbeName = ".write-probe";
		private const string SnapshotPrefix = "_legacy-backup-";

		private static SaveMode? cachedMode;
		private static string? cachedRoot;

		#region 路径

		public static string DownloadDir
		{
			get
			{
				try
				{
					string? p = Android.OS.Environment
						.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)?
						.AbsolutePath;
					if (!string.IsNullOrEmpty(p))
						return p;
				}
				catch
				{
				}
				return "/storage/emulated/0/Download";
			}
		}

		/// <summary>/sdcard/Download/Celeste</summary>
		public static string ExternalRoot => Path.Combine(DownloadDir, GameFolder);

		/// <summary>旧位置 / 内部影子：files/userdata/Celeste，只读源，永不删除。</summary>
		public static string InternalRoot(Context context) =>
			Path.Combine(context.FilesDir!.AbsolutePath, "userdata", GameFolder);

		#endregion

		#region 权限

		public static bool HasAllFilesAccess()
		{
			if (Build.VERSION.SdkInt < BuildVersionCodes.R)
				return true;
			try
			{
				return Android.OS.Environment.IsExternalStorageManager;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>跳到系统的「所有文件访问」授权页。</summary>
		public static bool RequestAllFilesAccess(Activity activity)
		{
			if (Build.VERSION.SdkInt < BuildVersionCodes.R)
				return false;
			try
			{
				Intent intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission);
				intent.SetData(AndroidUri.Parse("package:" + activity.PackageName));
				activity.StartActivity(intent);
				return true;
			}
			catch (Exception e)
			{
				Log.Warn(Tag, "打开授权页失败: " + e.Message);
				try
				{
					activity.StartActivity(new Intent(Settings.ActionManageAllFilesAccessPermission));
					return true;
				}
				catch (Exception e2)
				{
					Log.Warn(Tag, "兜底授权页也失败: " + e2.Message);
					return false;
				}
			}
		}

		private static bool CanWrite(string dir)
		{
			try
			{
				Directory.CreateDirectory(dir);
				string probe = Path.Combine(dir, ProbeName);
				File.WriteAllText(probe, "ok");
				File.Delete(probe);
				return true;
			}
			catch (Exception e)
			{
				Log.Info(Tag, $"不可写 {dir}: {e.GetType().Name}");
				return false;
			}
		}

		#endregion

		#region 解析

		public static (SaveMode Mode, string Root) Resolve(Context context)
		{
			if (cachedMode.HasValue && cachedRoot != null)
				return (cachedMode.Value, cachedRoot);

			string ext = ExternalRoot;
			if (HasAllFilesAccess() && CanWrite(ext))
			{
				cachedMode = SaveMode.External;
				cachedRoot = ext;
			}
			else
			{
				cachedMode = SaveMode.Internal;
				cachedRoot = InternalRoot(context);
				try
				{
					Directory.CreateDirectory(cachedRoot);
				}
				catch (Exception e)
				{
					Log.Error(Tag, "内部目录建不了: " + e.Message);
				}
			}

			Log.Info(Tag, $"存档模式={cachedMode} 根={cachedRoot}");
			return (cachedMode.Value, cachedRoot);
		}

		/// <summary>
		/// 传给 CelesteAndroid.HostConfig.PrefPath 的值。
		/// SDL_GetPrefPath 会再拼一层 "Celeste"，所以这里返回父目录：
		///   External → /sdcard/Download
		///   Internal → files/userdata
		/// </summary>
		public static string ResolvePrefPath(Context context)
		{
			(SaveMode _, string root) = Resolve(context);
			string? parent = Path.GetDirectoryName(root);
			return string.IsNullOrEmpty(parent) ? root : parent;
		}

		#endregion

		#region 启动引导

		/// <summary>
		/// 启动时调用。顺序：解析 → 必要时从影子回灌 → 必要时迁移旧存档。
		/// 任何异常都吞掉并记录，绝不阻断游戏启动。
		/// </summary>
		public static void EnsureReady(Context context)
		{
			try
			{
				(SaveMode mode, string root) = Resolve(context);

				if (mode == SaveMode.External)
				{
					RestoreFromShadowIfEmpty(context, root);
					TryMigrateLegacy(context, root);
				}
				else
				{
					Log.Info(Tag, "内部模式：等待授权后迁移到 Download/Celeste");
				}
			}
			catch (Exception e)
			{
				Log.Error(Tag, "EnsureReady 异常，游戏继续启动: " + e);
			}
		}

		#endregion

		#region 迁移

		private static string SavesOf(string root) => Path.Combine(root, SavesFolder);

		private static bool HasCelesteFiles(string dir) =>
			Directory.Exists(dir) && Directory.GetFiles(dir, "*.celeste").Length > 0;

		/// <summary>
		/// 幂等迁移：目标已有存档就跳过；否则从旧位置复制并逐文件校验。
		/// 源目录只读，不删不移。
		/// </summary>
		private static void TryMigrateLegacy(Context context, string targetRoot)
		{
			string targetSaves = SavesOf(targetRoot);
			if (HasCelesteFiles(targetSaves))
			{
				Log.Info(Tag, "目标已有存档，跳过迁移");
				return;
			}

			string files = context.FilesDir!.AbsolutePath;
			var candidates = new List<string>
			{
				InternalRoot(context),                                        // files/userdata/Celeste
				Path.Combine(files, "userdata"),                              // files/userdata（存档直接在这一层）
				Path.Combine(files, "Saves"),                                 // 早期构建
				Path.Combine(files, "Celeste", SavesFolder)                    // 早期构建
			};

			var plan = new List<(string Src, string Dst)>();
			foreach (string src in candidates.Distinct())
			{
				if (!Directory.Exists(src))
					continue;

				CollectSaves(src, targetSaves, plan);
				CollectSaves(Path.Combine(src, SavesFolder), targetSaves, plan);

				string settingsSrc = Path.Combine(src, SettingsFile);
				if (File.Exists(settingsSrc))
					plan.Add((settingsSrc, Path.Combine(targetRoot, SettingsFile)));

				string backupsSrc = Path.Combine(src, BackupsFolder);
				if (Directory.Exists(backupsSrc))
				{
					foreach (string f in Directory.GetFiles(backupsSrc, "*", SearchOption.AllDirectories))
					{
						string rel = Path.GetRelativePath(backupsSrc, f);
						plan.Add((f, Path.Combine(targetRoot, BackupsFolder, rel)));
					}
				}
			}

			if (plan.Count == 0)
			{
				Log.Info(Tag, "没有找到旧存档");
				return;
			}

			// 同一目标只保留第一个来源
			var unique = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach ((string src, string dst) in plan)
			{
				if (!unique.ContainsKey(dst))
					unique[dst] = src;
			}

			var written = new List<(string Dst, string Hash)>();
			try
			{
				foreach (KeyValuePair<string, string> pair in unique)
				{
					Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
					File.Copy(pair.Value, pair.Key, overwrite: false);
					written.Add((pair.Key, Sha256(pair.Key)));
					Log.Info(Tag, "复制 " + Path.GetFileName(pair.Key));
				}
			}
			catch (Exception e)
			{
				Log.Error(Tag, "迁移写入失败，回滚已复制文件: " + e.Message);
				Rollback(written);
				return;
			}

			foreach ((string dst, string expected) in written)
			{
				if (!File.Exists(dst) || Sha256(dst) != expected)
				{
					Log.Error(Tag, "校验失败，回滚: " + dst);
					Rollback(written);
					return;
				}
			}

			Log.Info(Tag, $"迁移完成并校验通过，共 {written.Count} 个文件");

			try
			{
				SnapshotLegacy(context);
			}
			catch (Exception e)
			{
				Log.Warn(Tag, "快照失败但不影响迁移: " + e.Message);
			}
		}

		/// <summary>强制再跑一次迁移（UI 按钮用）。</summary>
		public static int ForceMigrate(Context context)
		{
			(SaveMode mode, string root) = Resolve(context);
			if (mode == SaveMode.Internal)
				throw new InvalidOperationException(
					"需要「所有文件访问」权限才能把存档放到 Download/Celeste。");

			string targetSaves = SavesOf(root);
			if (HasCelesteFiles(targetSaves))
				return 0;

			int before = Directory.Exists(targetSaves)
				? Directory.GetFiles(targetSaves, "*.celeste").Length
				: 0;
			TryMigrateLegacy(context, root);
			int after = Directory.Exists(targetSaves)
				? Directory.GetFiles(targetSaves, "*.celeste").Length
				: 0;
			return Math.Max(0, after - before);
		}

		private static void CollectSaves(string srcDir, string targetSaves, List<(string, string)> plan)
		{
			if (!Directory.Exists(srcDir))
				return;
			foreach (string f in Directory.GetFiles(srcDir, "*.celeste"))
				plan.Add((f, Path.Combine(targetSaves, Path.GetFileName(f))));
		}

		private static void Rollback(List<(string Dst, string Hash)> written)
		{
			foreach ((string dst, string _) in written)
			{
				try
				{
					if (File.Exists(dst))
						File.Delete(dst);
				}
				catch (Exception e)
				{
					Log.Warn(Tag, "回滚删除失败 " + dst + ": " + e.Message);
				}
			}
		}

		private static string Sha256(string file)
		{
			using SHA256 sha = SHA256.Create();
			using FileStream fs = File.OpenRead(file);
			return Convert.ToHexString(sha.ComputeHash(fs));
		}

		/// <summary>把旧存档根整棵压成 zip 放到 Download/Celeste。</summary>
		private static void SnapshotLegacy(Context context)
		{
			string legacy = InternalRoot(context);
			if (!Directory.Exists(legacy))
				return;

			string target = ExternalRoot;
			if (!CanWrite(target))
				return;

			string zip = Path.Combine(target, SnapshotPrefix
				+ DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
			if (File.Exists(zip))
				return;

			ZipFile.CreateFromDirectory(legacy, zip, CompressionLevel.Optimal, includeBaseDirectory: true);
			Log.Info(Tag, "快照已写入 " + zip);
		}

		#endregion

		#region 回灌

		/// <summary>
		/// 外部根为空但内部影子有存档：Download 被清空或换机，自动回灌。
		/// 只在外部 Saves 完全没有 .celeste 时触发，不覆盖用户现有文件。
		/// </summary>
		private static void RestoreFromShadowIfEmpty(Context context, string externalRoot)
		{
			string extSaves = SavesOf(externalRoot);
			if (HasCelesteFiles(extSaves))
				return;

			string shadowRoot = InternalRoot(context);
			string shadowSaves = SavesOf(shadowRoot);
			if (!HasCelesteFiles(shadowSaves))
				return;

			Log.Warn(Tag, "Download 存档缺失，从内部影子回灌");
			Directory.CreateDirectory(extSaves);
			foreach (string f in Directory.GetFiles(shadowSaves, "*.celeste"))
			{
				string dst = Path.Combine(extSaves, Path.GetFileName(f));
				try
				{
					File.Copy(f, dst, overwrite: true);
					Log.Info(Tag, "回灌 " + Path.GetFileName(f));
				}
				catch (Exception e)
				{
					Log.Error(Tag, "回灌失败 " + Path.GetFileName(f) + ": " + e.Message);
				}
			}

			string shadowSettings = Path.Combine(shadowRoot, SettingsFile);
			if (File.Exists(shadowSettings))
			{
				try
				{
					File.Copy(shadowSettings, Path.Combine(externalRoot, SettingsFile), overwrite: true);
				}
				catch (Exception e)
				{
					Log.Warn(Tag, "回灌 settings 失败: " + e.Message);
				}
			}
		}

		#endregion

		#region 同步

		/// <summary>游戏暂停时调用，两个方向都写，保证永远有两份。</summary>
		public static void OnGamePause(Context context)
		{
			try
			{
				(SaveMode mode, string root) = Resolve(context);
				if (mode == SaveMode.External)
					MirrorToShadow(context, root);
				else
					MirrorToDownload(context, root);
			}
			catch (Exception e)
			{
				Log.Warn(Tag, "暂停同步失败: " + e.Message);
			}
		}

		/// <summary>外部模式：Download → 内部影子。</summary>
		private static void MirrorToShadow(Context context, string externalRoot)
		{
			string src = SavesOf(externalRoot);
			if (!HasCelesteFiles(src))
				return;

			string shadowRoot = InternalRoot(context);
			string dst = SavesOf(shadowRoot);
			Directory.CreateDirectory(dst);

			int n = 0;
			foreach (string f in Directory.GetFiles(src, "*.celeste"))
			{
				try
				{
					File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
					n++;
				}
				catch (Exception e)
				{
					Log.Warn(Tag, "影子备份失败 " + Path.GetFileName(f) + ": " + e.Message);
				}
			}

			string settings = Path.Combine(externalRoot, SettingsFile);
			if (File.Exists(settings))
			{
				try
				{
					File.Copy(settings, Path.Combine(shadowRoot, SettingsFile), overwrite: true);
				}
				catch
				{
				}
			}

			Log.Info(Tag, $"影子备份 {n} 个存档");
		}

		/// <summary>内部模式：内部 → MediaStore Download/Celeste。</summary>
		private static void MirrorToDownload(Context context, string internalRoot)
		{
			string src = SavesOf(internalRoot);
			if (!HasCelesteFiles(src))
				return;

			int n = 0;
			foreach (string f in Directory.GetFiles(src, "*.celeste"))
				n += PutToDownloads(context, f, "Download/Celeste/Saves");

			string settings = Path.Combine(internalRoot, SettingsFile);
			if (File.Exists(settings))
				n += PutToDownloads(context, settings, "Download/Celeste");

			Log.Info(Tag, $"MediaStore 镜像 {n} 个文件");
		}

		private static int PutToDownloads(Context context, string localFile, string relativeDir)
		{
			ContentResolver resolver = context.ContentResolver!;
			string name = Path.GetFileName(localFile);
			AndroidUri collection = MediaStore.Downloads.ExternalContentUri!;

			try
			{
				string where = MediaStore.IMediaColumns.DisplayName + "=? AND "
					+ MediaStore.IMediaColumns.RelativePath + "=?";
				resolver.Delete(collection, where, new[] { name, relativeDir + "/" });
			}
			catch
			{
			}

			var values = new ContentValues();
			values.Put(MediaStore.IMediaColumns.DisplayName, name);
			values.Put(MediaStore.IMediaColumns.MimeType, "application/octet-stream");
			values.Put(MediaStore.IMediaColumns.RelativePath, relativeDir);
			values.Put(MediaStore.IMediaColumns.IsPending, 1);

			AndroidUri? item = resolver.Insert(collection, values);
			if (item == null)
				return 0;

			using (Stream output = resolver.OpenOutputStream(item)!)
			using (FileStream input = File.OpenRead(localFile))
				input.CopyTo(output);

			values.Clear();
			values.Put(MediaStore.IMediaColumns.IsPending, 0);
			resolver.Update(item, values, null, null);
			return 1;
		}

		#endregion

		#region 手动导出

		/// <summary>SAF 手动导出到用户选的目录（不依赖任何权限）。</summary>
		public static int ExportToTree(Context context, AndroidUri treeUri)
		{
			(SaveMode _, string root) = Resolve(context);
			string savesDir = SavesOf(root);
			if (!HasCelesteFiles(savesDir))
				throw new InvalidOperationException("还没有存档，先进游戏玩一次。");

			string[] saves = Directory.GetFiles(savesDir, "*.celeste");
			ContentResolver resolver = context.ContentResolver!;
			string treeId = DocumentsContract.GetTreeDocumentId(treeUri)!;
			string folderName = "Celeste-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
			AndroidUri parent = DocumentsContract.BuildDocumentUriUsingTree(treeUri, treeId)!;
			AndroidUri sub = DocumentsContract.CreateDocument(
				resolver, parent, DocumentsContract.Document.MimeTypeDir, folderName)!;
			string subId = DocumentsContract.GetDocumentId(sub)!;

			foreach (string save in saves)
			{
				AndroidUri doc = DocumentsContract.CreateDocument(
					resolver,
					DocumentsContract.BuildDocumentUriUsingTree(treeUri, subId)!,
					"application/octet-stream",
					Path.GetFileName(save))!;
				using Stream output = resolver.OpenOutputStream(doc)!;
				using FileStream input = File.OpenRead(save);
				input.CopyTo(output);
			}

			string settings = Path.Combine(root, SettingsFile);
			if (File.Exists(settings))
			{
				AndroidUri doc = DocumentsContract.CreateDocument(
					resolver,
					DocumentsContract.BuildDocumentUriUsingTree(treeUri, subId)!,
					"application/octet-stream",
					SettingsFile)!;
				using Stream output = resolver.OpenOutputStream(doc)!;
				using FileStream input = File.OpenRead(settings);
				input.CopyTo(output);
			}

			return saves.Length;
		}

		#endregion
	}
}
