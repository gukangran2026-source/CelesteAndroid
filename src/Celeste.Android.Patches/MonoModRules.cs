using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace MonoMod
{
	/// <summary>
	/// Android 启动适配的 IL hook。
	///
	/// MonoMod 25.0.1 的 IL hook 机制（反编译 MonoMod.Patcher.dll 确认）：
	///
	///   1. ParseRulesInType 遍历 mod 里每个类型，遇到某个「属性类型」带
	///      [MonoModCustomMethodAttribute("处理方法名")] 时，把它登记进
	///      CustomMethodAttributeHandlers：key = 该属性类型全名，
	///      value = 在 MonoMod.MonoModRules 类里找同名静态方法。
	///
	///   2. DefaultPostProcessType → RunCustomAttributeHandlers 扫目标方法上的属性，
	///      命中登记的 key 就调对应处理方法，签名必须是
	///      static void X(object self, object[] args)，args[0] 是目标方法的 ILContext。
	///
	/// 关键约束（踩了很久）：
	/// - 这个类必须精确叫 MonoMod.MonoModRules，MonoMod 按全名查找。
	/// - 这个类的方法体里不能出现对 mod 内其它类型的直接引用。
	///   MonoMod 的 relinker 会把 MonoMod.MonoModRules 里所有类型引用
	///   当成需要重映射的目标，找不到就抛
	///   RelinkFailedException: failed relinking &lt;类型名&gt;。
	///   所以这里全部走 Type.GetType("全名, 程序集名") 字符串查找。
	/// </summary>
	public static class MonoModRules
	{
		/// <summary>被 [AndroidBoot] 标注的方法走这里。args[0] 是目标方法的 ILContext。</summary>
		public static void ApplyAndroidBoot(object self, object[] args)
		{
			ILContext il = (ILContext)args[0];
			Console.WriteLine("[EverestAndroid] IL hook 命中: " + il.Method.FullName);

			// ---- A) 入口注入 BootInit() ----
			// 用字符串查类型和方法，避免 relink 阶段解析 mod 内部类型。
			MethodInfo bootInit = ResolveBootInit();
			if (bootInit == null)
			{
				Console.WriteLine("[EverestAndroid] 找不到 BootPatches.BootInit，跳过入口注入");
			}
			else
			{
				ILCursor c = new(il);
				c.Index = 0;
				c.Emit(OpCodes.Call, bootInit);
				Console.WriteLine("[EverestAndroid] 入口注入完成");
			}

			// ---- B) 跳过 SELinux execheap 探测 ----
			Mono.Collections.Generic.Collection<Instruction> ins = il.Body.Instructions;

			int callIdx = -1;
			for (int i = 0; i < ins.Count; i++)
			{
				Instruction x = ins[i];
				if (x.OpCode == OpCodes.Call || x.OpCode == OpCodes.Callvirt)
				{
					MethodReference mr = x.Operand as MethodReference;
					if (mr != null && mr.Name == "IsOSPlatform")
					{
						callIdx = i;
						break;
					}
				}
			}

			if (callIdx < 0)
			{
				Console.WriteLine("[EverestAndroid] 未找到 IsOSPlatform，跳过探测补丁");
				return;
			}

			int brIdx = -1;
			for (int i = callIdx + 1; i < ins.Count && i <= callIdx + 8; i++)
			{
				OpCode op = ins[i].OpCode;
				if (op == OpCodes.Brfalse || op == OpCodes.Brfalse_S)
				{
					brIdx = i;
					break;
				}
			}

			if (brIdx < 0)
			{
				Console.WriteLine("[EverestAndroid] 未找到 brfalse，跳过探测补丁");
				return;
			}

			Instruction branch = ins[brIdx];
			Instruction target = branch.Operand as Instruction;
			branch.OpCode = OpCodes.Br;
			branch.Operand = target;
			Console.WriteLine("[EverestAndroid] 已跳过 execheap 探测 -> " + target);
		}

		/// <summary>按全名字符串找 BootPatches.BootInit，不产生类型引用。</summary>
		private static MethodInfo ResolveBootInit()
		{
			try
			{
				Assembly self = typeof(MonoModRules).Assembly;
				Type t = self.GetType("CelesteAndroid.Everest.BootPatches", throwOnError: false);
				if (t == null)
					return null;
				return t.GetMethod("BootInit", BindingFlags.Public | BindingFlags.Static);
			}
			catch (Exception e)
			{
				Console.WriteLine("[EverestAndroid] 解析 BootInit 失败: " + e.Message);
				return null;
			}
		}
	}
}