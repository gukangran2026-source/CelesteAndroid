using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod;

namespace CelesteAndroid.Patcher
{
	/// <summary>
	/// Gera um Celeste.dll que roda no .NET moderno a partir do Celeste.exe original do usuário.
	///
	/// Fluxo:
	///   1. Lê Celeste.exe (build FNA).
	///   2. Aplica os módulos MonoMod na ordem recebida:
	///      Celeste.Mod.mm.dll (Everest) → Celeste.Android.mm.dll → Celeste.Everest.Android.mm.dll.
	///      O Everest precisa vir primeiro: ele reescreve o entry point e injeta Celeste.Mod.*,
	///      os patches seguintes dependem desses tipos já existirem.
	///   3. AutoPatch.
	///   4. Ajustes finais (tirar flag 32-bit, adaptar BOOT.Main para Android).
	///   5. Escreve.
	/// </summary>
	public static class CelestePatcher
	{
		/// <param name="celesteExe">Celeste.exe original (build FNA).</param>
		/// <param name="modAssemblies">Módulos MonoMod, na ordem de aplicação.</param>
		/// <param name="outputDll">Destino do Celeste.dll patcheado.</param>
		/// <param name="dependencyDirs">Onde achar FNA.dll, Steamworks.NET.dll etc. (os nossos, não os do jogo).</param>
		public static void Patch(
			string celesteExe,
			IEnumerable<string> modAssemblies,
			string outputDll,
			IEnumerable<string> dependencyDirs,
			Action<string>? log = null)
		{
			log ??= Console.WriteLine;
			List<string> mods = modAssemblies.Where(File.Exists).ToList();
			if (mods.Count == 0)
				throw new FileNotFoundException("Nenhum módulo MonoMod para aplicar.");

			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputDll))!);

			using LoggingModder modder = new(log)
			{
				InputPath = celesteExe,
				OutputPath = outputDll,
				// Deferred: o Cecil só resolve o que usa. No Android não há mscorlib/System.Runtime como
				// arquivos, e o modo Immediate tenta resolver os tipos de todos os atributos.
				ReadingMode = ReadingMode.Deferred,
				MissingDependencyThrow = false,
			};
			modder.DependencyDirs.AddRange(dependencyDirs);

			modder.Read();
			foreach (string mod in mods)
			{
				log("[patcher] aplicando " + Path.GetFileName(mod));
				modder.ReadMod(mod);
			}
			modder.MapDependencies();
			modder.AutoPatch();

			ModuleDefinition module = modder.Module;

			// O Celeste.exe vem marcado como x86 (32BITREQUIRED); em ARM64/x64 isso impede o carregamento.
			module.Attributes &= ~(ModuleAttributes.Required32Bit | ModuleAttributes.Preferred32Bit);
			module.Attributes |= ModuleAttributes.ILOnly;
			module.Architecture = TargetArchitecture.I386;

			// Adaptações do Everest que não dá para fazer por atributo MonoMod.
			AndroidBootPatcher.Apply(module, log);

			modder.Write();
			log($"Celeste patcheado: {outputDll}");
		}

		private sealed class LoggingModder(Action<string> log) : MonoModder
		{
			public override void Log(string text) => log("[MonoMod] " + text);
		}
	}

	/// <summary>
	/// Ajustes de IL no Everest já mesclado, específicos de Android.
	///
	/// BOOT.Main faz três coisas que quebram no Android:
	///   1. PathGame = Path.GetDirectoryName(Assembly.Location) → diretório não gravável.
	///   2. if (IsOSPlatform(Linux)) { mprotect(PROT_EXEC) } → SELinux recusa, lança Win32Exception.
	///      No Android IsOSPlatform(Linux) é true, então esse ramo sempre entra.
	///   3. Process.Start em si mesmo (StartCelesteProcess / StartVanilla) → não há executável.
	///
	/// Aplicado aqui, e não por atributo MonoMod, porque o módulo de regras precisa ficar em
	/// MonoMod.MonoModRules e o relinker não resolve tipos de outros namespaces nesse contexto.
	/// Mexer direto no Cecil é determinístico e verificável.
	/// </summary>
	internal static class AndroidBootPatcher
	{
		public static void Apply(ModuleDefinition module, Action<string> log)
		{
			TypeDefinition? boot = module.GetType("Celeste.Mod.BOOT");
			if (boot == null)
			{
				log("[android] Celeste.Mod.BOOT ausente, pulando adaptações do Everest");
				return;
			}

			MethodDefinition? main = boot.Methods.FirstOrDefault(m => m.Name == "Main");
			if (main == null || !main.HasBody)
			{
				log("[android] BOOT.Main ausente");
				return;
			}

			// 1) Injeta BootInit() no início: TMPDIR + Mods dir + PathGame.
			MethodDefinition? bootInit = FindBootInit(module);
			if (bootInit != null)
			{
				ILProcessor il = main.Body.GetILProcessor();
				il.InsertBefore(main.Body.Instructions[0], il.Create(OpCodes.Call, bootInit));
				log("[android] BOOT.Main: BootInit injetado");
			}
			else
			{
				log("[android] BootInit não encontrado, sem injeção de entrada");
			}

			// 2) Pula a sonda SELinux execheap.
			SkipExecheapProbe(main, log);

			// 3) Process.Start não existe no Android.
			ReplaceBody(boot, "StartCelesteProcess", new[] { OpCodes.Ldnull, OpCodes.Ret }, log);
			ReplaceBody(boot, "StartVanilla", new[] { OpCodes.Ret }, log);
			ReplaceBody(boot, "RestartViaLauncher", new[] { OpCodes.Ldc_I4_0, OpCodes.Ret }, log);
		}

		private static MethodDefinition? FindBootInit(ModuleDefinition module)
		{
			TypeDefinition? t = module.GetType("CelesteAndroid.Everest.BootPatches");
			return t?.Methods.FirstOrDefault(m => m.Name == "BootInit" && m.Parameters.Count == 0);
		}

		/// <summary>
		/// Troca o brfalse que guarda o ramo IsOSPlatform(Linux) por um br incondicional.
		/// A janela de 8 instruções cobre o padrão bne.un.s + brfalse do compilador.
		/// </summary>
		private static void SkipExecheapProbe(MethodDefinition main, Action<string> log)
		{
			var ins = main.Body.Instructions;

			int callIdx = -1;
			for (int i = 0; i < ins.Count; i++)
			{
				Instruction x = ins[i];
				if ((x.OpCode == OpCodes.Call || x.OpCode == OpCodes.Callvirt)
					&& x.Operand is MethodReference mr
					&& mr.Name == "IsOSPlatform")
				{
					callIdx = i;
					break;
				}
			}

			if (callIdx < 0)
			{
				log("[android] BOOT.Main: IsOSPlatform não encontrado");
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
				log("[android] BOOT.Main: brfalse não encontrado");
				return;
			}

			Instruction branch = ins[brIdx];
			Instruction? target = branch.Operand as Instruction;
			branch.OpCode = OpCodes.Br;
			branch.Operand = target;
			log("[android] BOOT.Main: sonda execheap ignorada");
		}

		private static void ReplaceBody(
			TypeDefinition type, string name, OpCode[] body, Action<string> log)
		{
			MethodDefinition? m = type.Methods.FirstOrDefault(x => x.Name == name);
			if (m == null || !m.HasBody)
			{
				log("[android] " + name + " ausente");
				return;
			}

			ILProcessor il = m.Body.GetILProcessor();
			m.Body.Instructions.Clear();
			m.Body.Variables.Clear();
			m.Body.ExceptionHandlers.Clear();
			foreach (OpCode op in body)
				il.Append(il.Create(op));
			log("[android] " + name + ": corpo substituído");
		}
	}
}