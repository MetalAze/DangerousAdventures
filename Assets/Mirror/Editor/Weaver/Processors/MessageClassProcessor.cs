// this class generates OnSerialize/OnDeserialize when inheriting from MessageBase

using System.Linq;
using Mono.CecilX;
using Mono.CecilX.Cil;

namespace Mirror.Weaver
{
    internal static class MessageClassProcessor
    {
        private static bool IsEmptyDefault(this MethodBody body)
        {
            return body.Instructions.All(instruction =>
                instruction.OpCode == OpCodes.Nop || instruction.OpCode == OpCodes.Ret);
        }

        public static void Process(TypeDefinition td)
        {
            Weaver.DLog(td, "MessageClassProcessor Start");

            GenerateSerialization(td);
            if (Weaver.WeavingFailed) return;

            GenerateDeSerialization(td);
            Weaver.DLog(td, "MessageClassProcessor Done");
        }

        private static void GenerateSerialization(TypeDefinition td)
        {
            Weaver.DLog(td, "  GenerateSerialization");
            var existingMethod = td.Methods.FirstOrDefault(md => md.Name == "Serialize");
            if (existingMethod != null && !existingMethod.Body.IsEmptyDefault()) return;

            if (td.Fields.Count == 0) return;

            // check for self-referencing types
            foreach (var field in td.Fields)
                if (field.FieldType.FullName == td.FullName)
                {
                    Weaver.Error($"{td} has field ${field} that references itself");
                    return;
                }

            var serializeFunc = existingMethod ?? new MethodDefinition("Serialize",
                                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                                    Weaver.voidType);

            if (existingMethod == null) //only add to new method
                serializeFunc.Parameters.Add(new ParameterDefinition("writer", ParameterAttributes.None,
                    Weaver.CurrentAssembly.MainModule.ImportReference(Weaver.NetworkWriterType)));
            var serWorker = serializeFunc.Body.GetILProcessor();
            if (existingMethod != null)
                serWorker.Body.Instructions.Clear(); //remove default nop&ret from existing empty interface method

            if (!td.IsValueType) //if not struct(IMessageBase), likely same as using else {} here in all cases
            {
                // call base
                var baseSerialize = Resolvers.ResolveMethodInParents(td.BaseType, Weaver.CurrentAssembly, "Serialize");
                if (baseSerialize != null)
                {
                    serWorker.Append(serWorker.Create(OpCodes.Ldarg_0)); // base
                    serWorker.Append(serWorker.Create(OpCodes.Ldarg_1)); // writer
                    serWorker.Append(serWorker.Create(OpCodes.Call, baseSerialize));
                }
            }

            foreach (var field in td.Fields)
            {
                if (field.IsStatic || field.IsPrivate || field.IsSpecialName)
                    continue;

                var writeFunc = Writers.GetWriteFunc(field.FieldType);
                if (writeFunc != null)
                {
                    serWorker.Append(serWorker.Create(OpCodes.Ldarg_1));
                    serWorker.Append(serWorker.Create(OpCodes.Ldarg_0));
                    serWorker.Append(serWorker.Create(OpCodes.Ldfld, field));
                    serWorker.Append(serWorker.Create(OpCodes.Call, writeFunc));
                }
                else
                {
                    Weaver.Error($"{field} has unsupported type");
                    return;
                }
            }

            serWorker.Append(serWorker.Create(OpCodes.Ret));

            if (existingMethod == null) //only add if not just replaced body
                td.Methods.Add(serializeFunc);
        }

        private static void GenerateDeSerialization(TypeDefinition td)
        {
            Weaver.DLog(td, "  GenerateDeserialization");
            var existingMethod = td.Methods.FirstOrDefault(md => md.Name == "Deserialize");
            if (existingMethod != null && !existingMethod.Body.IsEmptyDefault()) return;

            if (td.Fields.Count == 0) return;

            var serializeFunc = existingMethod ?? new MethodDefinition("Deserialize",
                                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                                    Weaver.voidType);

            if (existingMethod == null) //only add to new method
                serializeFunc.Parameters.Add(new ParameterDefinition("reader", ParameterAttributes.None,
                    Weaver.CurrentAssembly.MainModule.ImportReference(Weaver.NetworkReaderType)));
            var serWorker = serializeFunc.Body.GetILProcessor();
            if (existingMethod != null)
                serWorker.Body.Instructions.Clear(); //remove default nop&ret from existing empty interface method

            if (!td.IsValueType) //if not struct(IMessageBase), likely same as using else {} here in all cases
            {
                // call base
                var baseDeserialize =
                    Resolvers.ResolveMethodInParents(td.BaseType, Weaver.CurrentAssembly, "Deserialize");
                if (baseDeserialize != null)
                {
                    serWorker.Append(serWorker.Create(OpCodes.Ldarg_0)); // base
                    serWorker.Append(serWorker.Create(OpCodes.Ldarg_1)); // writer
                    serWorker.Append(serWorker.Create(OpCodes.Call, baseDeserialize));
                }
            }

            foreach (var field in td.Fields)
            {
                if (field.IsStatic || field.IsPrivate || field.IsSpecialName)
                    continue;

                var readerFunc = Readers.GetReadFunc(field.FieldType);
                if (readerFunc != null)
                {
                    serWorker.Append(serWorker.Create(OpCodes.Ldarg_0));
                    serWorker.Append(serWorker.Create(OpCodes.Ldarg_1));
                    serWorker.Append(serWorker.Create(OpCodes.Call, readerFunc));
                    serWorker.Append(serWorker.Create(OpCodes.Stfld, field));
                }
                else
                {
                    Weaver.Error($"{field} has unsupported type");
                    return;
                }
            }

            serWorker.Append(serWorker.Create(OpCodes.Ret));

            if (existingMethod == null) //only add if not just replaced body
                td.Methods.Add(serializeFunc);
        }
    }
}