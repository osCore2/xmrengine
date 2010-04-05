/***************************************************\
 *  COPYRIGHT 2010, Mike Rieker, Beverly, MA, USA  *
 *  All rights reserved.                           *
\***************************************************/

using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_Key = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_Rotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;

using Mono.Tasklets;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

/**
 * @brief Wrapper class for ILGenerator
 *        It can write out debug output.
 */

namespace OpenSim.Region.ScriptEngine.XMREngine
{
	public enum ScriptMyILGenCode : byte {
		BegMethod, EndMethod, TheEnd,
		DclLabel, DclLocal, DclMethod, MarkLabel,
		EmitNull, EmitField, EmitLocal, EmitType, EmitLabel, EmitMethodExt, 
		EmitMethodInt, EmitCtor, EmitDouble, EmitFloat, EmitInteger, EmitString,
	}

	public delegate void ScriptMyILGenEndMethod (DynamicMethod method);

	public class ScriptMyILGen
	{
		private static readonly int OPCSTRWIDTH = 12;

		private static Dictionary<short, OpCode> opCodes = PopulateOpCodes ();
		private static Dictionary<string, Type> string2Type = PopulateS2T ();
		private static Dictionary<Type, string> type2String = PopulateT2S ();

		private static MethodInfo monoGetCurrentOffset = typeof (ILGenerator).GetMethod ("Mono_GetCurrentOffset",
						BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, 
						new Type[] { typeof (ILGenerator) }, null);

		private string methName;
		private BinaryWriter objWriter;

		private int labelNumber = 0;
		private int localNumber = 0;

		/**
		 * @brief Begin fucntion declaration
		 * @param methName = name of the method being declared
		 * @param retType = its return value type
		 * @param argTypes[] = its argument types
		 * @param objWriter = file to write its object code to
		 *
		 * After calling this function, the following functions should be called:
		 *    this.BegMethod ();
		 *      this.<as required> ();
		 *    this.EndMethod ();
		 *
		 * The design of this object is such that many constructors may be called,
		 * but once a BegMethod() is called for one of the objects, no method may
		 * called for any of the other objects until EndMethod() is called (or it 
		 * would break up the object stream for that method).  But we need to have
		 * many constructors possible so we get function headers at the beginning
		 * of the object file in case there are forward references to the functions.
		 */
		public ScriptMyILGen (string methName, Type retType, Type[] argTypes, BinaryWriter objWriter)
		{
			this.methName  = methName;
			this.objWriter = objWriter;

			/*
			 * This tells the reader to call 'new DynamicMethod()' to create
			 * the function header.  Then any forward reference calls to this
			 * method will have a MethodInfo struct to call.
			 */
			objWriter.Write ((byte)ScriptMyILGenCode.DclMethod);
			objWriter.Write (methName);
			objWriter.Write (GetStrFromType (retType));

			int nArgs = argTypes.Length;
			objWriter.Write (nArgs);
			for (int i = 0; i < nArgs; i ++) {
				objWriter.Write (GetStrFromType (argTypes[i]));
			}
		}

		/**
		 * @brief Begin outputting object code for the function
		 */
		public void BegMethod ()
		{
			/*
			 * This tells the reader to call methodInfo.GetILGenerator()
			 * so it can start writing CIL code for the method.
			 */
			objWriter.Write ((byte)ScriptMyILGenCode.BegMethod);
			objWriter.Write (methName);
		}

		/**
		 * @brief End of object code for the function
		 */
		public void EndMethod ()
		{
			/*
			 * This tells the reader that all code for the method has
			 * been written and so it will typically call CreateDelegate()
			 * to finalize the method and create an entrypoint.
			 */
			objWriter.Write ((byte)ScriptMyILGenCode.EndMethod);

			objWriter = null;
		}

		/**
		 * @brief Declare a local variable for use by the function
		 */
		public ScriptMyLocal DeclareLocal (Type type, string name)
		{
			ScriptMyLocal myLocal = new ScriptMyLocal ();
			myLocal.type   = type;
			myLocal.name   = name;
			myLocal.number = localNumber ++;

			objWriter.Write ((byte)ScriptMyILGenCode.DclLocal);
			objWriter.Write (myLocal.number);
			objWriter.Write (myLocal.name);
			objWriter.Write (GetStrFromType (type));

			return myLocal;
		}

		/**
		 * @brief Define a label for use by the function
		 */
		public ScriptMyLabel DefineLabel (string name)
		{
			ScriptMyLabel myLabel = new ScriptMyLabel ();
			myLabel.name   = name;
			myLabel.number = labelNumber ++;

			objWriter.Write ((byte)ScriptMyILGenCode.DclLabel);
			objWriter.Write (myLabel.number);
			objWriter.Write (myLabel.name);

			return myLabel;
		}

		/**
		 * @brief Emit opcodes and various operands as part of function body
		 */
		public void Emit (OpCode opcode)
		{
			objWriter.Write ((byte)ScriptMyILGenCode.EmitNull);
			WriteOpCode (opcode);
		}

		public void Emit (OpCode opcode, FieldInfo field)
		{
			objWriter.Write ((byte)ScriptMyILGenCode.EmitField);
			WriteOpCode (opcode);
			objWriter.Write (GetStrFromType (field.ReflectedType));
			objWriter.Write (field.Name);
		}

		public void Emit (OpCode opcode, ScriptMyLocal myLocal)
		{
			objWriter.Write ((byte)ScriptMyILGenCode.EmitLocal);
			WriteOpCode (opcode);
			objWriter.Write (myLocal.number);
		}

		public void Emit (OpCode opcode, Type type)
		{
			objWriter.Write ((byte)ScriptMyILGenCode.EmitType);
			WriteOpCode (opcode);
			objWriter.Write (GetStrFromType (type));
		}

		public void Emit (OpCode opcode, ScriptMyLabel myLabel)
		{
			objWriter.Write ((byte)ScriptMyILGenCode.EmitLabel);
			WriteOpCode (opcode);
			objWriter.Write (myLabel.number);
		}

		public void Emit (OpCode opcode, ScriptMyILGen method)
		{
			if (method == null) throw new ArgumentNullException ("method");
			objWriter.Write ((byte)ScriptMyILGenCode.EmitMethodInt);
			WriteOpCode (opcode);
			objWriter.Write (method.methName);
		}

		public void Emit (OpCode opcode, MethodInfo method)
		{
			objWriter.Write ((byte)ScriptMyILGenCode.EmitMethodExt);
			WriteOpCode (opcode);
			objWriter.Write (method.Name);
			objWriter.Write (GetStrFromType (method.ReflectedType));
			ParameterInfo[] parms = method.GetParameters ();
			int nArgs = parms.Length;
			objWriter.Write (nArgs);
			for (int i = 0; i < nArgs; i ++) {
				objWriter.Write (GetStrFromType (parms[i].ParameterType));
			}
		}

		public void Emit (OpCode opcode, ConstructorInfo ctor)
		{
			objWriter.Write ((byte)ScriptMyILGenCode.EmitCtor);
			WriteOpCode (opcode);
			objWriter.Write (GetStrFromType (ctor.ReflectedType));
			ParameterInfo[] parms = ctor.GetParameters ();
			int nArgs = parms.Length;
			objWriter.Write (nArgs);
			for (int i = 0; i < nArgs; i ++) {
				objWriter.Write (GetStrFromType (parms[i].ParameterType));
			}
		}

		public void Emit (OpCode opcode, double value)
		{
			objWriter.Write ((byte)ScriptMyILGenCode.EmitDouble);
			WriteOpCode (opcode);
			objWriter.Write (value);
		}

		public void Emit (OpCode opcode, float value)
		{
			objWriter.Write ((byte)ScriptMyILGenCode.EmitFloat);
			WriteOpCode (opcode);
			objWriter.Write (value);
		}

		public void Emit (OpCode opcode, int value)
		{
			objWriter.Write ((byte)ScriptMyILGenCode.EmitInteger);
			WriteOpCode (opcode);
			objWriter.Write (value);
		}

		public void Emit (OpCode opcode, string value)
		{
			objWriter.Write ((byte)ScriptMyILGenCode.EmitString);
			WriteOpCode (opcode);
			objWriter.Write (value);
		}

		/**
		 * @brief Declare that the target of a label is the next instruction.
		 */
		public void MarkLabel (ScriptMyLabel myLabel)
		{
			objWriter.Write ((byte)ScriptMyILGenCode.MarkLabel);
			objWriter.Write (myLabel.number);
		}

		/**
		 * @brief Write end-of-file marker to binary file.
		 */
		public static void TheEnd (BinaryWriter objWriter)
		{
			objWriter.Write ((byte)ScriptMyILGenCode.TheEnd);
		}

		/**
		 * @brief Take an object file created by ScriptMyILGen() and convert it to a series of dynamic methods.
		 * @param objReader = where to read object file from (as written by ScriptMyILGen above).
		 * @param endMethod = called for each method defined
		 * @param debWriter = debug file writer (or null if not wanted)
		 */
		public static void CreateObjCode (BinaryReader objReader, ScriptMyILGenEndMethod endMethod, StreamWriter debWriter)
		{
			Dictionary<string, DynamicMethod> methods = new Dictionary<string, DynamicMethod> ();
			DynamicMethod method = null;
			ILGenerator ilGen = null;
			Dictionary<int, Label> labels = new Dictionary<int, Label> ();
			Dictionary<int, LocalBuilder> locals = new Dictionary<int, LocalBuilder> ();
			Dictionary<int, string> labelNames = new Dictionary<int, string> ();
			Dictionary<int, string> localNames = new Dictionary<int, string> ();
			StringBuilder dbg = new StringBuilder ();
			object[] ilGenArg = new object[1];
			int offset = 0;

			while (true) {

				/*
				 * Clear debug line (.xmrasm file) output buffer.
				 */
				dbg.Remove (0, dbg.Length);

				/*
				 * Get IL instruction offset at beginning of instruction.
				 */
				offset = 0;
				if (ilGen != null) {
					offset = (int)monoGetCurrentOffset.Invoke (null, ilGenArg);
				}

				/*
				 * Read and decode next internal format code from input file (.xmrobj file).
				 */
				ScriptMyILGenCode code = (ScriptMyILGenCode)objReader.ReadByte ();
				switch (code) {

					/*
					 * Reached end-of-file so we are all done.
					 */
					case ScriptMyILGenCode.TheEnd: {
						if (debWriter != null) debWriter.WriteLine ("TheEnd.");
						return;
					}

					/*
					 * Beginning of method's contents.
					 * Method must have already been declared via DclMethod
					 * so all we need is its name to retrieve from methods[].
					 */
					case ScriptMyILGenCode.BegMethod: {
						string methName = objReader.ReadString ();

						method = methods[methName];
						ilGen  = method.GetILGenerator ();
						ilGenArg[0] = ilGen;

						labels.Clear ();
						locals.Clear ();
						labelNames.Clear ();
						localNames.Clear ();

						dbg.Append (methName);
						dbg.Append ("(...) {");
						break;
					}

					/*
					 * End of method's contents (ie, an OpCodes.Ret was probably just output).
					 * Call the callback to tell it the method is complete, and it can do whatever
					 * it wants with the method.
					 */
					case ScriptMyILGenCode.EndMethod: {
						ilGen = null;
						ilGenArg[0] = null;
						endMethod (method);
						dbg.Append ("}");
						break;
					}

					/*
					 * Declare a label for branching to.
					 */
					case ScriptMyILGenCode.DclLabel: {
						int number  = objReader.ReadInt32 ();
						string name = objReader.ReadString ();

						labels.Add (number, ilGen.DefineLabel ());
						labelNames.Add (number, name + "_" + number.ToString ());
						break;
					}

					/*
					 * Declare a local variable to store into.
					 */
					case ScriptMyILGenCode.DclLocal: {
						int number  = objReader.ReadInt32 ();
						string name = objReader.ReadString ();
						string type = objReader.ReadString ();
						Type syType = GetTypeFromStr (type);

						locals.Add (number, ilGen.DeclareLocal (syType));
						localNames.Add (number, name + "_" + number.ToString ());

						dbg.Append ("          ");
						dbg.Append (type.PadRight (OPCSTRWIDTH - 1));
						dbg.Append (" ");
						dbg.Append (localNames[number]);
						break;
					}

					/*
					 * Declare a method that will subsequently be defined.
					 * We create the DynamicMethod object at this point in case there
					 * are forward references from other method bodies.
					 */
					case ScriptMyILGenCode.DclMethod: {
						string methName = objReader.ReadString ();
						Type retType    = GetTypeFromStr (objReader.ReadString ());
						int nArgs       = objReader.ReadInt32 ();

						Type[] argTypes = new Type[nArgs];
						for (int i = 0; i < nArgs; i ++) {
							argTypes[i] = GetTypeFromStr (objReader.ReadString ());
						}
						methods.Add (methName, new DynamicMethod (methName, retType, argTypes));

						dbg.Append (retType.Name);
						dbg.Append (" ");
						dbg.Append (methName);
						dbg.Append ("(");
						for (int i = 0; i < nArgs; i ++) {
							if (i > 0) dbg.Append (",");
							dbg.Append (argTypes[i].Name);
						}
						dbg.Append (")");
						break;
					}

					/*
					 * Mark a previously declared label at this spot.
					 */
					case ScriptMyILGenCode.MarkLabel: {
						int number = objReader.ReadInt32 ();

						ilGen.MarkLabel (labels[number]);
						LinePrefix (offset, dbg, ilGenArg);

						dbg.Append (labelNames[number]);
						dbg.Append (":");
						break;
					}

					/*
					 * Emit an opcode with no operand.
					 */
					case ScriptMyILGenCode.EmitNull: {
						OpCode opCode = ReadOpCode (objReader);

						ilGen.Emit (opCode);

						LinePrefix (offset, dbg, ilGenArg, opCode);
						break;
					}

					/*
					 * Emit an opcode with a FieldInfo operand.
					 */
					case ScriptMyILGenCode.EmitField: {
						OpCode opCode      = ReadOpCode (objReader);
						Type reflectedType = GetTypeFromStr (objReader.ReadString ());
						string fieldName   = objReader.ReadString ();

						FieldInfo field    = reflectedType.GetField (fieldName);
						ilGen.Emit (opCode, field);
						LinePrefix (offset, dbg, ilGenArg, opCode);

						dbg.Append (reflectedType.Name);
						dbg.Append (":");
						dbg.Append (fieldName);
						dbg.Append (" -> ");
						dbg.Append (field.FieldType.Name);
						dbg.Append ("   (field)");
						break;
					}

					/*
					 * Emit an opcode with a LocalBuilder operand.
					 */
					case ScriptMyILGenCode.EmitLocal: {
						OpCode opCode = ReadOpCode (objReader);
						int number    = objReader.ReadInt32 ();

						ilGen.Emit (opCode, locals[number]);

						LinePrefix (offset, dbg, ilGenArg, opCode);
						dbg.Append (localNames[number]);
						dbg.Append ("   (local)");
						break;
					}

					/*
					 * Emit an opcode with a Type operand.
					 */
					case ScriptMyILGenCode.EmitType: {
						OpCode opCode = ReadOpCode (objReader);
						Type type     = GetTypeFromStr (objReader.ReadString ());

						ilGen.Emit (opCode, type);

						LinePrefix (offset, dbg, ilGenArg, opCode);
						dbg.Append (type.Name);
						dbg.Append ("   (type)");
						break;
					}

					/*
					 * Emit an opcode with a Label operand.
					 */
					case ScriptMyILGenCode.EmitLabel: {
						OpCode opCode = ReadOpCode (objReader);
						int number    = objReader.ReadInt32 ();

						ilGen.Emit (opCode, labels[number]);

						LinePrefix (offset, dbg, ilGenArg, opCode);
						dbg.Append (labelNames[number]);
						dbg.Append ("   (label)");
						break;
					}

					/*
					 * Emit an opcode with a MethodInfo operand (such as a call) of an external function.
					 */
					case ScriptMyILGenCode.EmitMethodExt: {
						OpCode opCode   = ReadOpCode (objReader);
						string methName = objReader.ReadString ();
						Type methType   = GetTypeFromStr (objReader.ReadString ());
						int nArgs       = objReader.ReadInt32 ();

						Type[] argTypes = new Type[nArgs];
						for (int i = 0; i < nArgs; i ++) {
							argTypes[i] = GetTypeFromStr (objReader.ReadString ());
						}
						MethodInfo methInfo = methType.GetMethod (methName, argTypes);
						if (methInfo == null) {
							Console.WriteLine ("CreateObjCode*: methName={0}", methName);
							Console.WriteLine ("CreateObjCode*: methType={0}", methType.Name);
							Console.WriteLine ("CreateObjCode*:    nArgs={0}", nArgs.ToString());
							for (int i = 0; i < nArgs; i ++) {
								Console.WriteLine ("CreateObjCode*:   arg{0}={1}", i, argTypes[i].Name);
							}
						}
						ilGen.Emit (opCode, methInfo);

						LinePrefix (offset, dbg, ilGenArg, opCode);
						dbg.Append (methType.Name);
						dbg.Append (":");
						dbg.Append (methName);
						dbg.Append ("(");
						for (int i = 0; i < nArgs; i ++) {
							if (i > 0) dbg.Append (",");
							dbg.Append (argTypes[i].Name);
						}
						dbg.Append (") -> ");
						dbg.Append (methInfo.ReturnType.Name);
						break;
					}

					/*
					 * Emit an opcode with a MethodInfo operand of an internal function
					 * (previously declared via DclMethod).
					 */
					case ScriptMyILGenCode.EmitMethodInt: {
						OpCode opCode   = ReadOpCode (objReader);
						string methName = objReader.ReadString ();

						MethodInfo methInfo = methods[methName];
						ilGen.Emit (opCode, methInfo);

						ParameterInfo[] parms = methInfo.GetParameters ();
						int nArgs = parms.Length;
						LinePrefix (offset, dbg, ilGenArg, opCode);
						dbg.Append (methName);
						dbg.Append ("(");
						for (int i = 0; i < nArgs; i ++) {
							if (i > 0) dbg.Append (",");
							dbg.Append (parms[i].ParameterType.Name);
						}
						dbg.Append (") -> ");
						dbg.Append (methInfo.ReturnType.Name);
						break;
					}

					/*
					 * Emit an opcode with a ConstructorInfo operand.
					 */
					case ScriptMyILGenCode.EmitCtor: {
						OpCode opCode   = ReadOpCode (objReader);
						Type ctorType   = GetTypeFromStr (objReader.ReadString ());
						int nArgs       = objReader.ReadInt32 ();
						Type[] argTypes = new Type[nArgs];
						for (int i = 0; i < nArgs; i ++) {
							argTypes[i] = GetTypeFromStr (objReader.ReadString ());
						}

						ConstructorInfo ctorInfo = ctorType.GetConstructor (argTypes);
						ilGen.Emit (opCode, ctorInfo);

						LinePrefix (offset, dbg, ilGenArg, opCode);
						dbg.Append (ctorType.Name);
						dbg.Append ("(");
						for (int i = 0; i < nArgs; i ++) {
							if (i > 0) dbg.Append (",");
							dbg.Append (argTypes[i].Name);
						}
						dbg.Append (")");
						break;
					}

					/*
					 * Emit an opcode with a constant operand of various types.
					 */
					case ScriptMyILGenCode.EmitDouble: {
						OpCode opCode = ReadOpCode (objReader);
						double value  = objReader.ReadDouble ();

						ilGen.Emit (opCode, value);

						LinePrefix (offset, dbg, ilGenArg, opCode);
						dbg.Append (value.ToString ());
						dbg.Append ("   (double)");
						break;
					}

					case ScriptMyILGenCode.EmitFloat: {
						OpCode opCode = ReadOpCode (objReader);
						float value   = objReader.ReadSingle ();

						ilGen.Emit (opCode, value);

						LinePrefix (offset, dbg, ilGenArg, opCode);
						dbg.Append (value.ToString ());
						dbg.Append ("   (float)");
						break;
					}

					case ScriptMyILGenCode.EmitInteger: {
						OpCode opCode = ReadOpCode (objReader);
						int value     = objReader.ReadInt32 ();

						ilGen.Emit (opCode, value);

						LinePrefix (offset, dbg, ilGenArg, opCode);
						dbg.Append (value.ToString ());
						dbg.Append ("   (int)");
						break;
					}

					case ScriptMyILGenCode.EmitString: {
						OpCode opCode = ReadOpCode (objReader);
						string value  = objReader.ReadString ();

						ilGen.Emit (opCode, value);

						LinePrefix (offset, dbg, ilGenArg, opCode);
						dbg.Append ("\"");
						dbg.Append (value);
						dbg.Append ("\"   (string)");
						break;
					}

					/*
					 * Who knows what?
					 */
					default: throw new Exception ("bad ScriptMyILGenCode " + ((byte)code).ToString ());
				}

				/*
				 * Write line to .xmrasm file.
				 */
				if ((debWriter != null) && (dbg.Length > 0)) {
					debWriter.WriteLine (dbg.ToString ());
				}
			}
		}

		private static void LinePrefix (int offset, StringBuilder dbg, object[] ilGenArg, OpCode opCode)
		{
			LinePrefix (offset, dbg, ilGenArg);
			dbg.Append ("  ");
			dbg.Append (opCode.ToString ().PadRight (OPCSTRWIDTH - 1));
			dbg.Append (" ");
		}

		private static void LinePrefix (int offset, StringBuilder dbg, object[] ilGenArg)
		{
			dbg.Append ("  ");
			dbg.Append (offset.ToString ("X4"));
			dbg.Append ("  ");
		}

		/**
		 * @brief Generate array to quickly translate OpCode.Value to full OpCode struct.
		 */
		private static Dictionary<short, OpCode> PopulateOpCodes ()
		{
			Dictionary<short, OpCode> opCodeDict = new Dictionary<short, OpCode> ();
			FieldInfo[] fields = typeof (OpCodes).GetFields ();
			short highest = 0;
			for (int i = 0; i < fields.Length; i ++) {
				OpCode opcode = (OpCode)fields[i].GetValue (null);
				opCodeDict.Add (opcode.Value, opcode);
				if (highest < opcode.Value) highest = opcode.Value;
			}
			return opCodeDict;
		}

		/**
		 * @brief Write opcode out to file.
		 */
		private void WriteOpCode (OpCode opcode)
		{
			objWriter.Write (opcode.Value);
		}

		/**
		 * @brief Read opcode in from file.
		 */
		private static OpCode ReadOpCode (BinaryReader objReader)
		{
			short value = objReader.ReadInt16 ();
			return opCodes[value];
		}

		/**
		 * @brief Create type<->string conversions.
		 *        Using Type.AssemblyQualifiedName is horribly inefficient
		 *        and all our types should be known.
		 */
		private static Dictionary<string, Type> PopulateS2T ()
		{
			Dictionary<string, Type> s2t = new Dictionary<string, Type> ();

			s2t.Add ("binopstr", typeof (BinOpStr));
			s2t.Add ("bool",     typeof (bool));
			s2t.Add ("double",   typeof (double));
			s2t.Add ("float",    typeof (float));
			s2t.Add ("inlfunc",  typeof (InlineFunction));
			s2t.Add ("int",      typeof (int));
			s2t.Add ("int*",     typeof (int).MakeByRefType ());
			s2t.Add ("lslfloat", typeof (LSL_Float));
			s2t.Add ("lslint",   typeof (LSL_Integer));
			s2t.Add ("lsllist",  typeof (LSL_List));
			s2t.Add ("lslrot",   typeof (LSL_Rotation));
			s2t.Add ("lslstr",   typeof (LSL_String));
			s2t.Add ("lslvec",   typeof (LSL_Vector));
			s2t.Add ("math",     typeof (Math));
			s2t.Add ("midround", typeof (MidpointRounding));
			s2t.Add ("mmruthr",  typeof (MMRUThread));
			s2t.Add ("object",   typeof (object));
			s2t.Add ("object*",  typeof (object).MakeByRefType ());
			s2t.Add ("object[]", typeof (object[]));
			s2t.Add ("scrbase",  typeof (ScriptBaseClass));
			s2t.Add ("scrcode",  typeof (ScriptCodeGen));
			s2t.Add ("scrcont",  typeof (ScriptContinuation));
			s2t.Add ("string",   typeof (string));
			s2t.Add ("typecast", typeof (TypeCast));
			s2t.Add ("void",     typeof (void));
			s2t.Add ("xmrarray", typeof (XMR_Array));
			s2t.Add ("xmrhelps", typeof (XMRHelpers));
			s2t.Add ("xmrinst",  typeof (XMRInstance));

			return s2t;
		}

		private static Dictionary<Type, string> PopulateT2S ()
		{
			Dictionary<string, Type> s2t = PopulateS2T ();
			Dictionary<Type, string> t2s = new Dictionary<Type, string> ();
			foreach (KeyValuePair<string, Type> kvp in s2t) {
				t2s.Add (kvp.Value, kvp.Key);
			}
			return t2s;
		}

		private static string GetStrFromType (Type t)
		{
			string s;
			if (!type2String.TryGetValue (t, out s)) {
				s = t.AssemblyQualifiedName;
			}
			return s;
		}

		private static Type GetTypeFromStr (string s)
		{
			Type t;
			if (!string2Type.TryGetValue (s, out t)) {
				t = Type.GetType(s, true);
			}
			return t;
		}
	}

	public class ScriptMyLabel {
		public string name;
		public int number;
	}

	public class ScriptMyLocal {
		public string name;
		public Type type;
		public int number;
	}
}