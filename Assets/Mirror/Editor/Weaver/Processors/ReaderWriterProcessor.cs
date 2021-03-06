using Mono.CecilX;
using UnityEditor.Compilation;
using System.IO;

namespace Mirror.Weaver
{
    public static class ReaderWriterProcessor
    {
        // find all readers and writers and register them
        public static void ProcessReadersAndWriters(AssemblyDefinition CurrentAssembly)
        {
            Readers.Init();
            Writers.Init();

            foreach (var unityAsm in CompilationPipeline.GetAssemblies())
                if (unityAsm.name != CurrentAssembly.Name.Name)
                    try
                    {
                        using (var asmResolver = new DefaultAssemblyResolver())
                        using (var assembly = AssemblyDefinition.ReadAssembly(unityAsm.outputPath,
                            new ReaderParameters
                                {ReadWrite = false, ReadSymbols = false, AssemblyResolver = asmResolver}))
                        {
                            ProcessAssemblyClasses(CurrentAssembly, assembly);
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        // During first import,  this gets called before some assemblies
                        // are built,  just skip them
                    }

            ProcessAssemblyClasses(CurrentAssembly, CurrentAssembly);
        }

        private static void ProcessAssemblyClasses(AssemblyDefinition CurrentAssembly, AssemblyDefinition assembly)
        {
            foreach (var klass in assembly.MainModule.Types)
                // extension methods only live in static classes
                // static classes are represented as sealed and abstract
                if (klass.IsAbstract && klass.IsSealed)
                {
                    LoadWriters(CurrentAssembly, klass);
                    LoadReaders(CurrentAssembly, klass);
                }
        }

        private static void LoadWriters(AssemblyDefinition currentAssembly, TypeDefinition klass)
        {
            // register all the writers in this class.  Skip the ones with wrong signature
            foreach (var method in klass.Methods)
            {
                if (method.Parameters.Count != 2)
                    continue;

                if (method.Parameters[0].ParameterType.FullName != "Mirror.NetworkWriter")
                    continue;

                if (method.ReturnType.FullName != "System.Void")
                    continue;

                if (method.GetCustomAttribute("System.Runtime.CompilerServices.ExtensionAttribute") == null)
                    continue;

                var dataType = method.Parameters[1].ParameterType;
                Writers.Register(dataType, currentAssembly.MainModule.ImportReference(method));
            }
        }

        private static void LoadReaders(AssemblyDefinition currentAssembly, TypeDefinition klass)
        {
            // register all the reader in this class.  Skip the ones with wrong signature
            foreach (var method in klass.Methods)
            {
                if (method.Parameters.Count != 1)
                    continue;

                if (method.Parameters[0].ParameterType.FullName != "Mirror.NetworkReader")
                    continue;

                if (method.ReturnType.FullName == "System.Void")
                    continue;

                if (method.GetCustomAttribute("System.Runtime.CompilerServices.ExtensionAttribute") == null)
                    continue;

                Readers.Register(method.ReturnType, currentAssembly.MainModule.ImportReference(method));
            }
        }
    }
}