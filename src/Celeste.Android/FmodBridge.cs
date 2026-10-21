using System;
using Android.Content;
using Android.Runtime;

namespace CelesteAndroid
{
	/// <summary>
	/// Ponte para org.fmod.FMOD por reflexão.
	///
	/// Motivo: o Javac do .NET Android SDK só roda em host x64
	/// (libZipSharpNative-3-3.so é x86-64 e falha em ARM),
	/// então não dá para usar o binding gerado do fmod.jar.
	///
	/// As classes org.fmod.* já existem no classes.dex do APK base,
	/// e o JavaClassLoader do Android resolve por nome. Chamamos por reflexão
	/// com a assinatura exata (Landroid/content/Context;)V.
	///
	/// Se a classe não existir (ex.: APK sem FMOD), apenas loga e segue —
	/// o jogo ainda sobe, só sem áudio FMOD.
	/// </summary>
	public static class FmodBridge
	{
		private const string ClassName = "org.fmod.FMOD";
		private const string Tag = GameActivity.LogTag;

		private static bool initTried;

		public static void Init(Context context)
		{
			if (initTried)
				return;
			initTried = true;

			try
			{
				// 先用 Java 反射拿 Class，再调静态方法 Init(Context)。
				IntPtr classPtr = JNIEnv.FindClass(ClassName);
				if (classPtr == IntPtr.Zero)
				{
					Android.Util.Log.Warn(Tag, "org.fmod.FMOD 不存在，跳过 FMOD.Init");
					return;
				}

				IntPtr methodId = JNIEnv.GetStaticMethodID(classPtr, "init",
					"(Landroid/content/Context;)V");
				if (methodId == IntPtr.Zero)
				{
					Android.Util.Log.Warn(Tag, "org.fmod.FMOD.init(Context) 未找到");
					return;
				}

				// Context 本身是 Java 对象，直接把它的 handle 传进去；不转移所有权。
				JNIEnv.CallStaticVoidMethod(classPtr, methodId,
					new JValue(new Java.Lang.Object(context.Handle,
						JniHandleOwnership.DoNotTransfer)));
				Android.Util.Log.Info(Tag, "FMOD.Init 已调用");
			}
			catch (Exception e)
			{
				Android.Util.Log.Warn(Tag, "FMOD.Init 失败: " + e.Message);
			}
		}

		public static void Close()
		{
			try
			{
				IntPtr classPtr = JNIEnv.FindClass(ClassName);
				if (classPtr == IntPtr.Zero)
					return;

				IntPtr methodId = JNIEnv.GetStaticMethodID(classPtr, "close", "()V");
				if (methodId == IntPtr.Zero)
					return;

				JNIEnv.CallStaticVoidMethod(classPtr, methodId);
				Android.Util.Log.Info(Tag, "FMOD.Close 已调用");
			}
			catch (Exception e)
			{
				Android.Util.Log.Warn(Tag, "FMOD.Close 失败: " + e.Message);
			}
		}
	}
}