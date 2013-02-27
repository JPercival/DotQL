using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Ancestry.QueryProcessor.Type;

namespace Ancestry.QueryProcessor.Compile
{
	public class Compiler
	{
		private CompilerOptions _options;
		private Emitter _emitter;
		public Emitter Emitter { get { return _emitter; } }

		// Scope management
		private Dictionary<Parse.Statement, Frame> _frames = new Dictionary<Parse.Statement, Frame>();
		/// <summary> Stack frames by DOM object. </summary>
		public Dictionary<Parse.Statement, Frame> Frames { get { return _frames; } }

		private Dictionary<object, Func<MethodContext, ExpressionContext?>> _writersBySymbol = new Dictionary<object, Func<MethodContext, ExpressionContext?>>();
		/// <summary> Emitter necessary to reference the given symbol. </summary>
		public Dictionary<object, Func<MethodContext, ExpressionContext?>> WritersBySymbol { get { return _writersBySymbol; } }

		private Dictionary<object, List<object>> _references = new Dictionary<object, List<object>>();
		/// <summary> References to a given symbol. </summary>
		public Dictionary<object, List<object>> References { get { return _references; } }

		public Frame _importFrame;
		public Frame _scriptFrame;
		private HashSet<Parse.Statement> _recursions = new HashSet<Parse.Statement>();
		private Dictionary<Parse.ModuleMember, Func<MemberInfo>> _uncompiledMembers = new Dictionary<Parse.ModuleMember, Func<MemberInfo>>();
		private Dictionary<Parse.ModuleMember, object> _compiledMembers = new Dictionary<Parse.ModuleMember, object>();

		// Using private constructor pattern because state spans single static call
		private Compiler() { }

		public static Runtime.ExecuteHandler CreateExecutable(CompilerOptions options, Parse.Script script)
		{
			return new Compiler().InternalCreateExecutable(options, script);
		}

		private Runtime.ExecuteHandler InternalCreateExecutable(CompilerOptions options, Parse.Script script)
		{
			_options = options;
			if (_options.ScalarTypes == null)
				_options.ScalarTypes = 
					new Dictionary<string, BaseType>
					{
						{ "System.String", SystemTypes.String },
						{ "System.Int32", SystemTypes.Integer },
						{ "System.Int64", SystemTypes.Long },
						{ "System.Boolean", SystemTypes.Boolean }
					};
			_emitter =
				new Emitter
				(
					new EmitterOptions
					{
						DebugOn = options.DebugOn,
						AssemblyName = options.AssemblyName,
						SourceFileName = options.SourceFileName,
						ScalarTypes = options.ScalarTypes
					}
				);

			var main = _emitter.DeclareMain();
			CompileScript(main, script);
			var program = _emitter.CompleteMain(main);

			return _emitter.Complete(program);
		}

		private IEnumerable<Runtime.ModuleTuple> GetModules()
		{
			return Runtime.Runtime.GetModulesRepository(_options.Factory).Get(null, null);
		}

		private void CompileScript(MethodContext method, Parse.Script script)
		{
			_importFrame = new Frame();
			_scriptFrame = AddFrame(_importFrame, script);

			// Create temporary frame for resolution of used modules from all modules
			var modulesFrame = new Frame();
			foreach (var module in GetModules())
				modulesFrame.Add(script, module.Name, module);

			// Usings
			foreach (var u in script.Usings.Union(_options.DefaultUsings).Distinct(new UsingComparer()))
				CompileUsing(method, _importFrame, modulesFrame, u);

			//// Module declarations
			//foreach (var m in script.Modules)
			//	CompileModule(method, _scriptFrame, m);

			// Vars
			foreach (var v in script.Vars)
			{
				CompileVar(method, _scriptFrame, v);
				_scriptFrame.Add(v.Name, v);
			}

			//// Assignments
			//foreach (var a in script.Assignments)
			//	CompileAssignment(method, _scriptFrame, a);

			// Return expression
			if (script.Expression != null)
				CompileResult(method, _scriptFrame, script.Expression);
			else
				method.IL.Emit(OpCodes.Ldnull);
		}

		public void ResolveListReferences(Frame frame, IEnumerable<Parse.QualifiedIdentifier> list)
		{
			foreach (var item in list)
				ResolveReference<object>(frame, item); 
		}

		/// <summary> Resolves a given reference and logs the reference. </summary>
		public T ResolveReference<T>(Frame frame, Parse.QualifiedIdentifier item)
		{
			var target = frame.Resolve<T>(item);
			GetTargetSources(target).Add(item);
			return target;
		}

		/// <summary> Resolves a given reference and logs the reference. </summary>
		public T ResolveReference<T>(Frame frame, Parse.Statement statement, Name name)
		{
			var target = frame.Resolve<T>(statement, name);
			GetTargetSources(target).Add(statement);
			return target;
		}

		private List<object> GetTargetSources(object target)
		{
			List<object> sources;
			if (!_references.TryGetValue(target, out sources))
			{
				sources = new List<object>();
				_references.Add(target, sources);
			}
			return sources;
		}

		//private void CompileAssignment(MethodContext method, Frame frame, Parse.ClausedAssignment assignment)
		//{
		//	// TODO: handling of for, let, and where for assignment

		//	var local = AddFrame(frame, assignment);
		//	foreach (var set in assignment.Assignments)
		//	{
		//		var compiledTarget = CompileExpression(local, set.Target);
		//		var compiledSource = CompileExpression(local, set.Source, compiledTarget.Type);
		//		if (IsRepository(compiledTarget.Type))
		//			block.Add
		//			(
		//				Expression.Call
		//				(
		//					compiledTarget, 
		//					compiledTarget.Type.GetMethod("Set"), 
		//					Expression.Constant(null, typeof(Parse.Expression)), 
		//					compiledSource
		//				)
		//			);
		//		else
		//			block.Add(Expression.Assign(compiledTarget, compiledSource));
		//	}
		//}

		private void CompileResult(MethodContext method, Frame frame, Parse.ClausedExpression expression)
		{
			var result = MaterializeRepository(method, CompileClausedExpression(method, frame, expression));

			// Box the result if needed
			var nativeType = result.Type.GetNative(_emitter);
			if (nativeType.IsValueType)
				method.IL.Emit(OpCodes.Box, nativeType);
		}

		private void CompileVar(MethodContext method, Frame frame, Parse.VarDeclaration declaration)
		{
			// Note: the parser will validate that either the type or initializer are given

			var name = Name.FromQualifiedIdentifier(declaration.Name);

			// Compile the (optional) type
			var type = declaration.Type != null 
				? CompileTypeDeclaration(frame, declaration.Type) : 
				null;

			// Compile the (optional) initializer
			ExpressionContext initializer = new ExpressionContext();
			LocalBuilder initializerVar = null;
			if (declaration.Initializer != null)
				initializer = CompileExpression(method, frame, declaration.Initializer, type) ;

			// Default the type to the initializer's type
			type = type ?? initializer.Type;
			var nativeType = type.GetNative(_emitter);

			// Initialize the initializer variable
			initializerVar = method.DeclareLocal(declaration.Initializer, nativeType, name.ToString() + "initializer");
			if (declaration.Initializer != null)
				method.IL.Emit(OpCodes.Stloc, initializerVar);
			else
			{
				method.IL.Emit(OpCodes.Ldloca, initializerVar);
				method.IL.Emit(OpCodes.Initobj, nativeType);
			}
			
			// Attempt type conversion
			if (declaration.Initializer != null && type != initializer.Type)
				Convert(method, initializer, type);

			// Create the variable
			var variable = method.DeclareLocal(declaration, nativeType, name.ToString());
			_writersBySymbol.Add
			(
				declaration, 
				m => { m.IL.Emit(OpCodes.Ldloc, variable); return new ExpressionContext(type); }
			);

			// Initialize variable:
			//  variable = Runtime.GetInitializer<nativeType>(initializer, args, <name>);
			method.IL.Emit(OpCodes.Ldloc, initializerVar);	// initializer
			method.IL.Emit(OpCodes.Ldarg_0);	// args
			Emitter.EmitName(method, declaration.Name, name.Components);	// name
			method.IL.EmitCall(OpCodes.Call, ReflectionUtility.RuntimeGetInitializer.MakeGenericMethod(nativeType), null);
			method.IL.Emit(OpCodes.Stloc, variable);
		}

		//private void CompileModule(MethodContext method, Frame frame, Parse.ModuleDeclaration module)
		//{
		//	// Create the class for the module
		//	var moduleType = TypeFromModule(frame, module);

		//	// Build the code to declare the module
		//	block.Add
		//	(
		//		Expression.Call
		//		(
		//			typeof(Runtime.Runtime).GetMethod("DeclareModule"),
		//			MakeNameConstant(Name.FromQualifiedIdentifier(module.Name)),
		//			Builder.Version(module.Version),
		//			Expression.Constant(moduleType, typeof(System.Type)),
		//			_factoryParam
		//		)
		//	);
		//}

		private void CompileUsing(MethodContext method, Frame frame, Frame modulesFrame, Parse.Using use)
		{
			var moduleName = Name.FromQualifiedIdentifier(use.Target);
			var module = ResolveReference<Runtime.ModuleTuple>(modulesFrame, use, moduleName);
			frame.Add(use, moduleName, module);

			// Determine the class of the module
			var moduleType = ResolveReference<Runtime.ModuleTuple>(frame, use.Target).Class;

			// Create a variable to hold the module instance
			var moduleVar = method.DeclareLocal(use, moduleType, (use.Alias ?? use.Target).ToString());
			_writersBySymbol.Add(moduleType, m => { m.IL.Emit(OpCodes.Ldloc, moduleVar); return null; });

			// Discover methods
			foreach (var methodInfo in module.Class.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
			{
				frame.Add(use, moduleName + Name.FromNative(methodInfo.Name), methodInfo);
				_emitter.ImportType(methodInfo.ReturnType);
				foreach (var parameter in methodInfo.GetParameters())
					_emitter.ImportType(parameter.ParameterType);
			}

			// Discover enums
			foreach (var type in module.Class.GetNestedTypes(BindingFlags.Public))
			{
				var enumName = moduleName + Name.FromNative(type.Name);
				frame.Add(use, enumName, type);
				foreach (var enumItem in type.GetFields(BindingFlags.Public | BindingFlags.Static))
					frame.Add(use, enumName + Name.FromNative(enumItem.Name), enumItem);
			}

			// Discover consts
			foreach (var field in module.Class.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => (f.Attributes & FieldAttributes.Literal) == FieldAttributes.Literal))
			{
				frame.Add(use, moduleName + Name.FromNative(field.Name), field);
				_emitter.ImportType(field.FieldType);
			}

			// Discover variables
			foreach (var field in module.Class.GetFields(BindingFlags.Public | BindingFlags.Instance))
			{
				frame.Add(use, moduleName + Name.FromNative(field.Name), field);
				var native = field.FieldType.GenericTypeArguments[0];
				_emitter.ImportType(native);
				var type = _emitter.TypeFromNative(native);
				_writersBySymbol.Add
				(
					field, 
					m => 
					{ 
						m.IL.Emit(OpCodes.Ldloc, moduleVar); 
						m.IL.Emit(OpCodes.Ldfld, field);
						return new ExpressionContext(type, field.FieldType); 
					}
				);
			}

			// Discover typedefs
			foreach (var field in module.Class.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => (f.Attributes & FieldAttributes.Literal) != FieldAttributes.Literal))
			{
				frame.Add(use, moduleName + Name.FromNative(field.Name), field);
				_emitter.ImportType(field.FieldType);
				var type = _emitter.TypeFromNative(field.FieldType);
				_writersBySymbol.Add(field, m => new ExpressionContext(type));
			}

			// Build code to construct instance and assign to variable
			method.IL.Emit(OpCodes.Newobj, moduleType.GetConstructor(new System.Type[] { }));
			// Initialize each variable bound to a repository
			foreach (var field in moduleType.GetFields(BindingFlags.Public | BindingFlags.Instance))
			{
				method.IL.Emit(OpCodes.Dup);
				method.IL.Emit(OpCodes.Ldarg_1);
				method.IL.Emit(OpCodes.Ldtoken, moduleType);
				method.IL.EmitCall(OpCodes.Call, ReflectionUtility.TypeGetTypeFromHandle, null);
				method.IL.Emit(OpCodes.Ldstr, field.Name);
				method.IL.EmitCall(OpCodes.Call, ReflectionUtility.NameFromNative, null);
				method.IL.EmitCall(OpCodes.Callvirt, typeof(Storage.IRepositoryFactory).GetMethod("GetRepository").MakeGenericMethod(field.FieldType.GenericTypeArguments), null);
				method.IL.Emit(OpCodes.Stfld, field);
			}
			method.IL.Emit(OpCodes.Stloc, moduleVar);
		}

		//private DebugInfoExpression GetDebugInfo(Parse.Statement statement)
		//{
		//	return Expression.DebugInfo
		//	(
		//		_symbolDocument, 
		//		statement.Line + 1, 
		//		statement.LinePos + 1, 
		//		(statement.EndLine < 0 ? statement.Line : statement.EndLine) + 1, 
		//		(statement.EndLinePos < 0 ? statement.LinePos : statement.EndLinePos) + 1
		//	);
		//}

		private ExpressionContext CompileClausedExpression(MethodContext method, Frame frame, Parse.ClausedExpression expression, BaseType typeHint = null)
		{
			var local = AddFrame(frame, expression);
			method.IL.BeginScope();
			try
			{
				if (expression.ForClauses.Count > 0)
				{
					var result = CompileForClause(method, local, 0, expression, false);
					method.IL.Emit(OpCodes.Ldloc, result.resultVar);
					
					// Reset the result var in case this expression is in a loop
					method.IL.Emit(OpCodes.Ldnull);
					method.IL.Emit(OpCodes.Stloc, result.resultVar);

					return result.context;
				}
				else
					return CompileClausedReturn(method, local, expression, typeHint);
			}
			finally
			{
				method.IL.EndScope();
			}
		}

		private ForResult CompileForClause(MethodContext method, Frame local, int i, Parse.ClausedExpression expression, bool listEncountered)
		{
			if (i < expression.ForClauses.Count)
			{
				var forClause = expression.ForClauses[i];

				// Compile target expression
				var forExpression = MaterializeRepository(method, CompileExpression(method, local, forClause.Expression));
				local.Add(forClause.Name, forClause);
				if (!(forExpression.Type is IComponentType))
					throw new CompilerException(forClause, CompilerException.Codes.InvalidForExpressionTarget, forExpression.Type);

				// Declare enumerator and item variables
				var elementType = ((IComponentType)forExpression.Type).Of;
				var nativeElementType = elementType.GetNative(_emitter);
				var enumerableType = typeof(IEnumerable<>).MakeGenericType(nativeElementType);
				var enumeratorType = typeof(IEnumerator<>).MakeGenericType(nativeElementType);
				var enumerator = method.DeclareLocal(forClause, enumeratorType, "enumerator" + Name.FromQualifiedIdentifier(forClause.Name).ToString());
				var forVariable = method.DeclareLocal(forClause, nativeElementType, Name.FromQualifiedIdentifier(forClause.Name).ToString());
				_writersBySymbol.Add(forClause, m => { m.IL.Emit(OpCodes.Ldloc, forVariable); return new ExpressionContext(elementType); });

				// enumerator = GetEnumerator()
				method.IL.EmitCall(OpCodes.Callvirt, enumerableType.GetMethod("GetEnumerator"), null);
				method.IL.Emit(OpCodes.Stloc, enumerator);

				// while (MoveNext)
				var loopStart = method.IL.DefineLabel();
				var loopEnd = method.IL.DefineLabel();
				method.IL.MarkLabel(loopStart);
				method.IL.Emit(OpCodes.Ldloc, enumerator);
				method.IL.EmitCall(OpCodes.Callvirt, ReflectionUtility.IEnumerableMoveNext, null);
				method.IL.Emit(OpCodes.Brfalse, loopEnd);

				// forVariable = enumerator.Current
				method.IL.Emit(OpCodes.Ldloc, enumerator);
				method.IL.EmitCall(OpCodes.Callvirt, enumeratorType.GetProperty("Current").GetGetMethod(), null);
				method.IL.Emit(OpCodes.Stloc, forVariable);

				if (expression.WhereClause != null)
				{
					// if (<where expression>) continue
					var whereResult = CompileExpression(method, local, expression.WhereClause, SystemTypes.Boolean);
					if (!(whereResult.Type is BooleanType))
						throw new CompilerException(expression.WhereClause, CompilerException.Codes.IncorrectType, whereResult.Type, SystemTypes.Boolean);
					method.IL.Emit(OpCodes.Brfalse, loopStart);
				}

				var result = CompileForClause(method, local, i + 1, expression, listEncountered | !(forExpression.Type is SetType));

				method.IL.Emit(OpCodes.Br, loopStart);
				method.IL.MarkLabel(loopEnd);

				return result;
			}
			else
			{
				// Compile the return block	and store it in a variable
				var returnBlock = CompileClausedReturn(method, local, expression);
				var returnVariable = method.DeclareLocal(expression.Expression, returnBlock.NativeType ?? returnBlock.Type.GetNative(_emitter), "return");
				method.IL.Emit(OpCodes.Stloc, returnVariable);

				// Determine the result type
				var resultType = listEncountered
					? (BaseType)new ListType { Of = returnBlock.Type }
					: new SetType { Of = returnBlock.Type };
				var resultNative = resultType.GetNative(_emitter);
				var resultVariable = method.DeclareLocal(expression.Expression, resultNative, "result");

				// Initialize the result array first time through
				var skipInit = method.IL.DefineLabel();
				method.IL.Emit(OpCodes.Ldloc, resultVariable);
				method.IL.Emit(OpCodes.Brtrue, skipInit);
				method.IL.Emit(OpCodes.Newobj, resultNative.GetConstructor(new System.Type[] { }));
				method.IL.Emit(OpCodes.Stloc, resultVariable);
				method.IL.MarkLabel(skipInit);

				// Add the current item to the result
				method.IL.Emit(OpCodes.Ldloc, resultVariable);
				method.IL.Emit(OpCodes.Ldloc, returnVariable);
				var addMethod = resultNative.GetMethod("Add");
				method.IL.EmitCall(OpCodes.Call, addMethod, null);
				if (addMethod.ReturnType != typeof(void))
					method.IL.Emit(OpCodes.Pop);	// ignore any add result

				return new ForResult { context = new ExpressionContext(resultType), resultVar = resultVariable };
			}
		}

		private struct ForResult
		{
			public ExpressionContext context;
			public LocalBuilder resultVar;
		}

		private ExpressionContext CompileClausedReturn(MethodContext method, Frame frame, Parse.ClausedExpression clausedExpression, BaseType typeHint = null)
		{
			// Create a variable for each let and initialize
			foreach (var let in clausedExpression.LetClauses)
			{
				var compiledExpression = CompileExpression(method, frame, let.Expression);
				var variable = method.DeclareLocal(let, compiledExpression.Type.GetNative(_emitter), Name.FromQualifiedIdentifier(let.Name).ToString());
				method.IL.Emit(OpCodes.Stloc, variable);
				_writersBySymbol.Add(let, m => { m.IL.Emit(OpCodes.Ldloc, variable); return compiledExpression; });
				frame.Add(let.Name, let);
			}

			// Add the expression to the body
			return CompileExpression(method, frame, clausedExpression.Expression, typeHint);
		}

		public ExpressionContext CompileExpression(MethodContext method, Frame frame, Parse.Expression expression, BaseType typeHint = null)
		{
			switch (expression.GetType().Name)
			{
				case "LiteralExpression": return CompileLiteral(method, frame, (Parse.LiteralExpression)expression, typeHint);
				case "BinaryExpression": return CompileBinaryExpression(method, frame, (Parse.BinaryExpression)expression, typeHint);
				case "UnaryExpression": return CompileUnaryExpression(method, frame, (Parse.UnaryExpression)expression, typeHint);
				case "ClausedExpression": return CompileClausedExpression(method, frame, (Parse.ClausedExpression)expression, typeHint);
				case "IdentifierExpression": return CompileIdentifierExpression(method, frame, (Parse.IdentifierExpression)expression, typeHint);
				case "TupleSelector": return CompileTupleSelector(method, frame, (Parse.TupleSelector)expression, typeHint);
				case "ListSelector": return CompileListSelector(method, frame, (Parse.ListSelector)expression, typeHint);
				case "SetSelector": return CompileSetSelector(method, frame, (Parse.SetSelector)expression, typeHint);
				case "FunctionSelector": return CompileFunctionSelector(method, frame, (Parse.FunctionSelector)expression, typeHint);
				case "CallExpression": return CompileCallExpression(method, frame, (Parse.CallExpression)expression, typeHint);
				case "RestrictExpression": return CompileRestrictExpression(method, frame, (Parse.RestrictExpression)expression, typeHint);
				default : throw new NotSupportedException(String.Format("Expression type {0} is not supported", expression.GetType().Name));
			}
		}

		private ExpressionContext CompileRestrictExpression(MethodContext method, Frame frame, Parse.RestrictExpression restrictExpression, BaseType typeHint)
		{
			var left = CompileExpression(method, frame, restrictExpression.Expression, typeHint);
			return left.Type.CompileRestrictExpression(method, this, frame, left, restrictExpression, typeHint);
		}

		private ExpressionContext CompileFunctionSelector(MethodContext method, Frame frame, Parse.FunctionSelector functionSelector, BaseType typeHint)
		{
			// Extract return type as the type hint
			if (typeHint is FunctionType)
				typeHint = ((FunctionType)typeHint).Type;
			else
				typeHint = null;

			var local = AddFrame(_importFrame, functionSelector);

			var type = new FunctionType();

			// Create a new private method within the same type as the current method
			var typeBuilder = (TypeBuilder)method.Builder.DeclaringType;
			var innerMethod = new MethodContext
			(
				typeBuilder.DefineMethod("Function" + functionSelector.GetHashCode(), MethodAttributes.Private | MethodAttributes.Static)
			);

			// Compile each parameter
			foreach (var p in functionSelector.Parameters)
			{
				local.Add(p.Name, p);
				var parameter = new FunctionParameter { Name = Name.FromQualifiedIdentifier(p.Name), Type = CompileTypeDeclaration(_importFrame, p.Type) };
				type.Parameters.Add(parameter);
			}

			// Set the native signature
			var nativeParamTypes = functionSelector.Parameters.Select((p, i) => type.Parameters[i].Type.GetNative(_emitter)).ToArray();
			innerMethod.Builder.SetSignature(typeof(VoidType), null, null, nativeParamTypes, null, null);

			// Define the parameter symbols
			var index = 0;
			foreach (var p in functionSelector.Parameters)
			{
				var paramType = type.Parameters[index];
				var paramBuilder = innerMethod.Builder.DefineParameter(++index, ParameterAttributes.In, paramType.Name.ToString());
				_writersBySymbol.Add
				(
					p,
					m =>
					{
						m.IL.Emit(OpCodes.Ldarg, paramBuilder.Position - 1);
						return new ExpressionContext(paramType.Type);
					}
				);
			}

			// Compile the body
			var expression = CompileExpression(innerMethod, local, functionSelector.Expression, typeHint);
			type.Type = expression.Type;
			innerMethod.Builder.SetReturnType(expression.NativeType ?? expression.Type.GetNative(_emitter));

			innerMethod.IL.Emit(OpCodes.Ret);

			// Instantiate a delegate pointing to the new method
			var delegateType = type.GetNative(_emitter);
			method.IL.Emit(OpCodes.Ldnull);	// instance
			method.IL.Emit(OpCodes.Ldftn, innerMethod.Builder);	// method
			method.IL.Emit(OpCodes.Newobj, delegateType.GetConstructor(new[] { typeof(object), typeof(IntPtr) }));

			return new ExpressionContext(type);
		}

		//private System.Type TypeFromModule(Frame frame, Parse.ModuleDeclaration moduleDeclaration)
		//{
		//	var local = AddFrame(frame, moduleDeclaration);

		//	// Gather the module's symbols
		//	foreach (var member in moduleDeclaration.Members)
		//	{
		//		local.Add(member.Name, member);

		//		// Populate qualified enumeration members
		//		var memberName = Name.FromQualifiedIdentifier(member.Name);
		//		if (member is Parse.EnumMember)
		//			foreach (var e in ((Parse.EnumMember)member).Values)
		//				local.Add(member, memberName + Name.FromQualifiedIdentifier(e), member);

		//		// HACK: Pre-discover sets of tuples (tables) because these may be needed by tuple references.  Would be better to separate symbol discovery from compilation for types.
		//		Parse.TypeDeclaration varType;
		//		if 
		//		(
		//			member is Parse.VarMember 
		//			&& (varType = ((Parse.VarMember)member).Type) is Parse.SetType 
		//			&& ((Parse.SetType)varType).Type is Parse.TupleType
		//		)
		//			EnsureTupleTypeSymbols(frame, (Parse.TupleType)((Parse.SetType)varType).Type);
		//	}

		//	var module = _emitter.BeginModule(moduleDeclaration.Name.ToString());
			
		//	foreach (var member in moduleDeclaration.Members)
		//	{
		//		switch (member.GetType().Name)
		//		{
		//			case "VarMember": 
		//				_uncompiledMembers.Add
		//				(
		//					member, 
		//					()=>
		//					{
		//						var varMember = (Parse.VarMember)member;
		//						var compiledType = CompileTypeDeclaration(local, varMember.Type);
		//						var result = _emitter.DeclareVariable(module, member.Name.ToString(), compiledType);
		//						_uncompiledMembers.Remove(member);
		//						_compiledMembers.Add(member, result);
		//						return result;
		//					}
		//				);
		//				break;

		//			case "TypeMember":
		//				_uncompiledMembers.Add
		//				(
		//					member, 
		//					()=>
		//					{
		//						var typeMember = (Parse.TypeMember)member;
		//						var compiledType = CompileTypeDeclaration(local, typeMember.Type);
		//						var result = _emitter.DeclareTypeDef(module, member.Name.ToString(), compiledType);
		//						_uncompiledMembers.Remove(member);
		//						_compiledMembers.Add(member, result);
		//						return result;
		//					}
		//				);
		//				break;

		//			case "EnumMember":
		//				_uncompiledMembers.Add
		//				(
		//					member, 
		//					()=>
		//					{
		//						var result = _emitter.DeclareEnum(module, member.Name.ToString(), from v in ((Parse.EnumMember)member).Values select v.ToString());
		//						_uncompiledMembers.Remove(member);
		//						_compiledMembers.Add(member, result);
		//						return result;
		//					}
		//				);
		//				break;

		//			case "ConstMember":
		//				_uncompiledMembers.Add
		//				(
		//					member, 
		//					()=>
		//					{
		//						MemberInfo result;
		//						var expression = CompileExpression(local, ((Parse.ConstMember)member).Expression);
		//						if (expression is LambdaExpression)
		//							result = _emitter.DeclareMethod(module, member.Name.ToString(), (LambdaExpression)expression);
		//						else
		//						{
		//							var expressionResult = CompileTimeEvaluate(expression);
		//							result = _emitter.DeclareConst(module, member.Name.ToString(), expressionResult, expression.Type);
		//						}
		//						_uncompiledMembers.Remove(member);
		//						_compiledMembers.Add(member, result);
		//						return result;
		//					}
		//				);
		//				break;

		//			default: throw new Exception("Internal Error: Unknown member type " + member.GetType().Name);
		//		}
		//	}

		//	// Compile in no particular order until all members are resolved
		//	while (_uncompiledMembers.Count > 0)
		//		_uncompiledMembers.First().Value();

		//	return _emitter.EndModule(module);
		//}

		//private static object CompileTimeEvaluate(Expression expression)
		//{
		//	var lambda = Expression.Lambda(expression);
		//	var compiled = lambda.Compile();
		//	var result = compiled.DynamicInvoke();
		//	return result;
		//}

		private ExpressionContext CompileCallExpression(MethodContext method, Frame frame, Parse.CallExpression callExpression, BaseType typeHint)
		{
			/* 
			 * Functions are implemented as delegates if referencing a variable, or as methods if referencing a constant.
			 * The logical type will always be a FunctionType, but the native type with either be a MethodInfo
			 * or a Delegate.
			 */

			// Compile expression
			var expression = MaterializeRepository(method, CompileExpression(method, frame, callExpression.Expression));
			if (!(expression.Type is FunctionType))
				throw new CompilerException(callExpression.Expression, CompilerException.Codes.CannotInvokeNonFunction);

			// Compile arguments
			var args = new ExpressionContext[callExpression.Arguments.Count];
			for (var i = 0; i < callExpression.Arguments.Count; i++)
				args[i] = MaterializeRepository(method, CompileExpression(method, frame, callExpression.Arguments[i]));

			if (expression.Member != null)
			{
				var methodType = (MethodInfo)(expression.Member);

				// Resolve generic arguments
				if (methodType.ContainsGenericParameters)
				{
					var genericArgs = methodType.GetGenericArguments();
					var resolved = new System.Type[genericArgs.Length];						      
					if (callExpression.TypeArguments.Count > 0)
					{
						for (var i = 0; i < resolved.Length; i++)
							resolved[i] = CompileTypeDeclaration(frame, callExpression.TypeArguments[i]).GetNative(_emitter);
					}
					else
					{
						var parameters = methodType.GetParameters();
						for (var i = 0; i < parameters.Length; i++)
							DetermineTypeParameters(callExpression, resolved, parameters[i].ParameterType, args[i].NativeType ?? args[i].Type.GetNative(_emitter));
						// TODO: Assert that all type parameters are resolved
					}
					methodType = methodType.MakeGenericMethod(resolved);
					// http://msdn.microsoft.com/en-us/library/system.reflection.methodinfo.makegenericmethod.aspx
				}
				method.IL.EmitCall(OpCodes.Call, methodType, null);
			}
			else 
			{
				var delegateType = expression.Type.GetNative(_emitter);
				method.IL.EmitCall(OpCodes.Callvirt, delegateType.GetMethod("Invoke"), null);
			}
			return new ExpressionContext(((FunctionType)expression.Type).Type);
		}

		private BaseType CompileTypeDeclaration(Frame frame, Parse.TypeDeclaration typeDeclaration)
		{
			switch (typeDeclaration.GetType().Name)
			{
				//case "OptionalType": return typeof(Nullable<>).MakeGenericType(CompileTypeDeclaration(frame, ((Parse.OptionalType)typeDeclaration).Type));
				case "ListType": return new ListType { Of = CompileTypeDeclaration(frame, ((Parse.ListType)typeDeclaration).Type) };
				case "SetType": return new SetType { Of = CompileTypeDeclaration(frame, ((Parse.SetType)typeDeclaration).Type) };
				//case "TupleType": return CompileTupleType(frame, (Parse.TupleType)typeDeclaration);
				//case "FunctionType": return CompileFunctionType(frame, (Parse.FunctionType)typeDeclaration);
				case "NamedType": return CompileNamedType(frame, (Parse.NamedType)typeDeclaration);
				default: throw new Exception("Unknown type declaration " + typeDeclaration.GetType().Name); 
			}
		}

		private BaseType CompileNamedType(Frame frame, Parse.NamedType namedType)
		{
			var target = ResolveReference<object>(frame, namedType.Target);
			Func<MethodContext, ExpressionContext?> result;
			if (_writersBySymbol.TryGetValue(target, out result))
				return result(null).Value.Type;
			//if (target is BaseType)
			//	return (BaseType)target;
			//else if (target is FieldInfo)
			//	return ((FieldInfo)target).FieldType;
			//else if (target is Parse.ModuleMember)
			//	return ((FieldBuilder)LazyCompileModuleMember(namedType, target)).FieldType;
			else
				throw new Exception("Internal Error: Named type is not the correct type");
		}

		private void EndRecursionCheck(Parse.Statement statement)
		{
			_recursions.Remove(statement);
		}

		private void BeginRecursionCheck(Parse.Statement statement)
		{
			if (!_recursions.Add(statement))
				throw new CompilerException(statement, CompilerException.Codes.RecursiveDeclaration);
		}

		private void EnsureTupleTypeSymbols(Frame frame, Parse.TupleType tupleType)
		{
			if (!_frames.ContainsKey(tupleType))
			{
				var local = AddFrame(frame, tupleType);

				foreach (var a in tupleType.Attributes)
					local.Add(a.Name, a);

				foreach (var k in tupleType.Keys)
					ResolveListReferences(local, k.AttributeNames);

				foreach (var r in tupleType.References)
					ResolveListReferences(local, r.SourceAttributeNames);
			}
		}

		private System.Type CompileTupleType(Frame frame, Parse.TupleType tupleType)
		{
			EnsureTupleTypeSymbols(frame, tupleType);

			var normalized = new Type.TupleType();

			// Attributes
			foreach (var a in tupleType.Attributes)
				normalized.Attributes.Add(Name.FromQualifiedIdentifier(a.Name), CompileTypeDeclaration(frame, a.Type));		// uses frame, not local

			// References
			foreach (var k in tupleType.Keys)
				normalized.Keys.Add(new Type.TupleKey { AttributeNames = IdentifiersToNames(k.AttributeNames) });

			// Keys
			foreach (var r in tupleType.References)
			{
				var target = ResolveReference<Parse.Statement>(frame, r.Target);
				if (target is Parse.VarMember)
				{
					// Get the tuple type for the table
					var targetTupleType = CheckTableType(r.Target, ((Parse.VarMember)target).Type);

					// Add references to each target attribute
					ResolveListReferences(_frames[targetTupleType], r.TargetAttributeNames);
				}
				normalized.References.Add
				(
					Name.FromQualifiedIdentifier(r.Name),
					new Type.TupleReference
					{
						SourceAttributeNames = IdentifiersToNames(r.SourceAttributeNames),
						Target = Name.FromQualifiedIdentifier(r.Target),
						TargetAttributeNames = IdentifiersToNames(r.TargetAttributeNames)
					}
				);
			}

			return _emitter.FindOrCreateNativeFromTupleType(normalized);
		}

		/// <summary> Validates that the given target type is a table (set or list of tuples) and returns the tuple type.</summary>
		private static Parse.TypeDeclaration CheckTableType(Parse.Statement statement, Parse.TypeDeclaration targetType)
		{
			if (!(targetType is Parse.NaryType))
				throw new CompilerException(statement, CompilerException.Codes.IncorrectTypeReferenced, "Set or List of Tuple", targetType.GetType().Name);
			var memberType = ((Parse.NaryType)targetType).Type;
			if (!(memberType is Parse.TupleType))
				throw new CompilerException(statement, CompilerException.Codes.IncorrectTypeReferenced, "Set or List of Tuple", targetType.GetType().Name);
			return memberType;
		}
		
		private System.Type CompileFunctionType(Frame frame, Parse.FunctionType functionType)
		{
			var types = new List<System.Type>(from p in functionType.Parameters select CompileTypeDeclaration(frame, p.Type).GetNative(_emitter));
			types.Add(CompileTypeDeclaration(frame, functionType.ReturnType).GetNative(_emitter));
			return System.Linq.Expressions.Expression.GetDelegateType(types.ToArray());
		}

		private static Name[] IdentifiersToNames(IEnumerable<Parse.QualifiedIdentifier> ids)
		{
			return (from n in ids select Name.FromQualifiedIdentifier(n)).ToArray();
		}

		private void DetermineTypeParameters(Parse.Statement statement, System.Type[] resolved, System.Type parameterType, System.Type argumentType)
		{
			// If the given parameter contains an unresolved generic type parameter, attempt to resolve using actual arguments
			if (parameterType.ContainsGenericParameters)
			{
				var paramArgs = parameterType.GetGenericArguments();
				var argArgs = argumentType.GetGenericArguments();
				if (paramArgs.Length != argArgs.Length)
					throw new CompilerException(statement, CompilerException.Codes.MismatchedGeneric, parameterType, argumentType);
				for (var i = 0; i < paramArgs.Length; i++)
					if (paramArgs[i].IsGenericParameter && resolved[paramArgs[i].GenericParameterPosition] == null)
						resolved[paramArgs[i].GenericParameterPosition] = argArgs[i];
					else 
						DetermineTypeParameters(statement, resolved, paramArgs[i], argArgs[i]);
			}
		}

		private ExpressionContext CompileSetSelector(MethodContext method, Frame frame, Parse.SetSelector setSelector, BaseType typeHint)
		{
			// Get the component type
			if (typeHint is SetType)
				typeHint = ((SetType)typeHint).Of;
			else
				typeHint = null;

			return EmitNarySelector(method, frame, new SetType(), setSelector, setSelector.Items, typeHint);
		}

		private ExpressionContext CompileListSelector(MethodContext method, Frame frame, Parse.ListSelector listSelector, BaseType typeHint)
		{
			// Get the component type
			if (typeHint is ListType)
				typeHint = ((ListType)typeHint).Of;
			else
				typeHint = null;

			return EmitNarySelector(method, frame, new ListType(), listSelector, listSelector.Items, typeHint);
		}

		private ExpressionContext EmitNarySelector(MethodContext method, Frame frame, NaryType naryType, Parse.Statement statement, List<Parse.Expression> items, BaseType elementTypeHint)
		{
			method.IL.BeginScope();
			LocalBuilder element = null;
			BaseType elementType = null;
			MethodInfo addMethod = null;
			if (items.Count > 0)
			{
				// Compile the first item outside of the loop to determine the data type
				var expression = MaterializeRepository(method, CompileExpression(method, frame, items[0], elementType ?? elementTypeHint));

				// Save the element into a local
				elementType = expression.Type;
				var native = elementType.GetNative(_emitter);
				element = method.DeclareLocal(statement, native, "element");
				method.IL.Emit(OpCodes.Stloc, element);

				// Construct the set/list			
				naryType.Of = elementType;
				var naryNative = naryType.GetNative(_emitter);
				// Attempt to find constructor that takes an initial capacity
				var constructor = naryNative.GetConstructor(new System.Type[] { typeof(int) });
				if (constructor == null)
					constructor = naryNative.GetConstructor(new System.Type[] { });
				else
					method.IL.Emit(OpCodes.Ldc_I4, items.Count);
				method.IL.Emit(OpCodes.Newobj, constructor);
				addMethod = naryNative.GetMethod("Add");

				Action performAdd = () =>
				{
					method.IL.Emit(OpCodes.Dup);	// collection
					method.IL.Emit(OpCodes.Ldloc, element);
					method.IL.Emit(OpCodes.Call, addMethod);
					if (addMethod.ReturnType != typeof(void))
						method.IL.Emit(OpCodes.Pop);	// ignore any add result
				};
				performAdd();

				// Add remaining items
				for (var i = 1; i < items.Count; i++)
				{
					expression = MaterializeRepository(method, CompileExpression(method, frame, items[i], elementType ?? elementTypeHint));

					// Convert the element and store into local
					if (elementType != expression.Type)
						Convert(method, expression, elementType);
					method.IL.Emit(OpCodes.Stloc, element);
					
					performAdd();
				}
			}
			else
			{
				// Construct empty list
				naryType.Of = elementTypeHint ?? SystemTypes.Void;
				method.IL.Emit(OpCodes.Newobj, naryType.GetNative(_emitter).GetConstructor(new System.Type[] { }));
			}
			method.IL.EndScope();

			return new ExpressionContext(naryType);
		}

		private ExpressionContext Convert(MethodContext method, ExpressionContext expression, BaseType target)
		{
			throw new NotImplementedException();
		}

		private ExpressionContext CompileTupleSelector(MethodContext method, Frame frame, Parse.TupleSelector tupleSelector, BaseType typeHint)
		{
			method.IL.BeginScope();

			var local = AddFrame(frame, tupleSelector);
			var tupleType = new Type.TupleType();
			var fieldVars = new Dictionary<string, LocalBuilder>();

			// Compile and resolve attributes
			foreach (var a in tupleSelector.Attributes)
			{
				var attributeName = Name.FromQualifiedIdentifier(EnsureAttributeName(a.Name, a.Value));
				var attributeNameAsString = attributeName.ToString();
				var valueExpression = MaterializeRepository(method, CompileExpression(method, frame, a.Value));		// uses frame not local (attributes shouldn't be visible to each other)
				var fieldVar = method.DeclareLocal(a, valueExpression.Type.GetNative(_emitter), attributeNameAsString);
				method.IL.Emit(OpCodes.Stloc, fieldVar);
				fieldVars.Add(attributeNameAsString, fieldVar);
				local.Add(a, attributeName, a);
				tupleType.Attributes.Add(attributeName, valueExpression.Type);
			}

			// Resolve source reference columns
			foreach (var k in tupleSelector.Keys)
			{
				ResolveListReferences(local, k.AttributeNames);

				tupleType.Keys.Add(Type.TupleKey.FromParseKey(k));
			}

			// Resolve key reference columns
			foreach (var r in tupleSelector.References)
			{
				ResolveListReferences(local, r.SourceAttributeNames);
				var target = ResolveReference<Parse.Statement>(_scriptFrame, r.Target);
				ResolveListReferences(_frames[target], r.TargetAttributeNames);

				tupleType.References.Add(Name.FromQualifiedIdentifier(r.Name), Type.TupleReference.FromParseReference(r));
			}

			var type = _emitter.FindOrCreateNativeFromTupleType(tupleType);
			var instance = method.DeclareLocal(tupleSelector, type, "tuple");

			// Initialize each field
			foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
			{
				method.IL.Emit(OpCodes.Ldloca, instance);
				var fieldVar = fieldVars[field.Name];
				method.IL.Emit(OpCodes.Ldloc, fieldVar);
				method.IL.Emit(OpCodes.Stfld, field);
			}

			method.IL.Emit(OpCodes.Ldloc, instance);
			method.IL.EndScope();

			return new ExpressionContext(tupleType);
		}

		private static Parse.QualifiedIdentifier EnsureAttributeName(Parse.QualifiedIdentifier name, Parse.Expression expression)
		{
			return name == null ? NameFromExpression(expression) : name; 
		}

		private static Parse.QualifiedIdentifier NameFromExpression(Parse.Expression expression)
		{
			if (expression is Parse.IdentifierExpression)
				return ((Parse.IdentifierExpression)expression).Target;
			else
				throw new CompilerException(expression, CompilerException.Codes.CannotInferNameFromExpression);
		}

		private string QualifiedIdentifierToName(Parse.QualifiedIdentifier qualifiedIdentifier)
		{
			return String.Join("_", qualifiedIdentifier.Components);
		}

		private ExpressionContext CompileIdentifierExpression(MethodContext method, Frame frame, Parse.IdentifierExpression identifierExpression, BaseType typeHint)
		{
			var symbol = ResolveReference<object>(frame, identifierExpression.Target);
			Func<MethodContext, ExpressionContext?> writer;
			if (_writersBySymbol.TryGetValue(symbol, out writer))
			{
				var result = writer(method);
				if (result.HasValue)
					return result.Value;
			}

			// Lazy-compile module member if needed
			symbol = LazyCompileModuleMember(identifierExpression, symbol);

			switch (symbol.GetType().Name)
			{
				// Method
				case "RuntimeMethodInfo": return EmitMethodReference(method, (MethodInfo)symbol);

				// Const
				case "MdFieldInfo":
				{
					var field = (FieldInfo)symbol;
					return _emitter.EmitLiteral(method, field.GetValue(null), _emitter.TypeFromNative(field.FieldType)); 
				}

				// Variable
				case "RtFieldInfo":
				{
					var field = (FieldInfo)symbol;

					EmitModuleInstance(method, field.DeclaringType);
					method.IL.Emit(OpCodes.Ldfld, field);
					
					var type = _emitter.TypeFromNative(field.FieldType);
					var native = type.GetNative(_emitter) == field.FieldType ? null : field.FieldType;
					return new ExpressionContext(type, native);
				}

				// TODO: enums and typedefs
				default:
					throw new CompilerException(identifierExpression, CompilerException.Codes.IdentifierNotFound, identifierExpression.Target);
			}
		}

		private ExpressionContext EmitMethodReference(MethodContext method, MethodInfo member)
		{
			EmitModuleInstance(method, member.DeclaringType);
			var type = new FunctionType();
			type.Parameters.AddRange
			(
				from p in member.GetParameters()
				select new FunctionParameter { Name = Name.FromNative(p.Name), Type = _emitter.TypeFromNative(p.ParameterType) }
			);
			type.Type = _emitter.TypeFromNative(member.ReturnType);
			return new ExpressionContext(type, member.DeclaringType, member);
		}

		private void EmitModuleInstance(MethodContext method, System.Type module)
		{
			Func<MethodContext, ExpressionContext?> writer;
			if (!_writersBySymbol.TryGetValue(module, out writer))
				throw new Exception(String.Format("Internal error: unable to find module ({0}).", module.ToString()));
			writer(method);
		}

		/// <summary> If the given expression is a repository reference, invokes the get to return a concrete value. </summary>
		public ExpressionContext MaterializeRepository(MethodContext method, ExpressionContext expression)
		{
			if (expression.IsRepository())
			{
				var naturalNative = expression.Type.GetNative(_emitter);
				method.IL.Emit(OpCodes.Ldnull);	// Condition
				method.IL.Emit(OpCodes.Ldnull);	// Order
				method.IL.EmitCall(OpCodes.Callvirt, (expression.NativeType ?? naturalNative).GetMethod("Get"), null);
				// Reset the native type to the repository's type parameter if unnatural, or null if natural
				expression.NativeType = 
					expression.NativeType != null && expression.NativeType.IsGenericType && expression.NativeType.GenericTypeArguments[0] != naturalNative
						? expression.NativeType.GenericTypeArguments[0]
						: null;
				return expression;
			}
			else
				return expression;
		}

		private object LazyCompileModuleMember(Parse.Statement statement, object symbol)
		{
			if (symbol is Parse.ModuleMember)
			{
				var member = (Parse.ModuleMember)symbol;
				BeginRecursionCheck(statement);
				try
				{
					Func<MemberInfo> compilation;
					if (_uncompiledMembers.TryGetValue(member, out compilation))
						compilation();
					symbol = _compiledMembers[member];
				}
				finally
				{
					EndRecursionCheck(statement);
				}
			}
			return symbol;
		}

		private ExpressionContext CompileBinaryExpression(MethodContext method, Frame frame, Parse.BinaryExpression expression, BaseType typeHint)
		{
			var left = CompileExpression(method, frame, expression.Left);
			return left.Type.CompileBinaryExpression(method, this, frame, left, expression, typeHint);
		}

		private ExpressionContext CompileUnaryExpression(MethodContext method, Frame frame, Parse.UnaryExpression expression, BaseType typeHint)
		{
			var inner = CompileExpression(method, frame, expression.Expression);
			return inner.Type.CompileUnaryExpression(method, this, frame, inner, expression, typeHint);
		}

		public Frame AddFrame(Frame parent, Parse.Statement statement)
		{
			var newFrame = new Frame(parent);
			_frames.Add(statement, newFrame);
			return newFrame;
		}

		private ExpressionContext CompileLiteral(MethodContext method, Frame frame, Parse.LiteralExpression expression, BaseType typeHint)
		{
			return _emitter.EmitLiteral(method, expression.Value, typeHint);
		}
	}
}

