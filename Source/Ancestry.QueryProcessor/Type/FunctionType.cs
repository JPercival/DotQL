﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ancestry.QueryProcessor;
using Ancestry.QueryProcessor.Compile;
using System.Reflection;
using System.Reflection.Emit;

namespace Ancestry.QueryProcessor.Type
{
	public class FunctionType : BaseType
	{
		private List<FunctionParameter> _parameters = new List<FunctionParameter>();
		public List<FunctionParameter> Parameters { get { return _parameters; } set { _parameters = value; } }

		// TODO: Type parameters
		//private List<FunctionTypeParameter> _parameters = new List<FunctionTypeParameter>();
		//public List<FunctionTypeParameter> Parameters { get { return _parameters; } }

		public BaseType Type { get; set; }

        public ExpressionContext CompileCallExpression(Compiler compiler, Frame frame, ExpressionContext function, Parse.CallExpression callExpression, BaseType typeHint, ExpressionContext[] args)
        {
			/* 
			 * Functions are implemented as delegates if referencing a variable, or as methods if referencing a constant.
			 * The logical type will always be a FunctionType, but the native type with either be a MethodInfo
			 * or a Delegate.
			 */

			//TODO: Verify that there is no case where this needed because generic types should be resolved by call resolution code.
            MethodInfo methodType = null;
            if (function.Member != null)
            {
                methodType = (MethodInfo)(function.Member);

                // Resolve generic arguments
                if (methodType.ContainsGenericParameters)
                {
                    var genericArgs = methodType.GetGenericArguments();
                    var resolved = new System.Type[genericArgs.Length];
                    if (callExpression.TypeArguments.Count > 0)
                    {
                        for (var i = 0; i < resolved.Length; i++)
                            resolved[i] = compiler.CompileTypeDeclaration(frame, callExpression.TypeArguments[i]).GetNative(compiler.Emitter);
                    }
                    else
                    {
                        var parameters = methodType.GetParameters();
                        for (var i = 0; i < parameters.Length; i++)
                            compiler.DetermineTypeParameters(callExpression, resolved, parameters[i].ParameterType, args[i].NativeType ?? args[i].Type.GetNative(compiler.Emitter));
                        // TODO: Assert that all type parameters are resolved
                    }
                    methodType = methodType.MakeGenericMethod(resolved);
                    // http://msdn.microsoft.com/en-us/library/system.reflection.methodinfo.makegenericmethod.aspx
                }
            }

            return
                new ExpressionContext
                (
                    callExpression,
                    ((FunctionType)function.Type).Type,
                    function.Characteristics,
                    m =>
                    {
                        if (methodType != null)		// Invoke as method
                        {
                            if (function.EmitGet != null)
                                function.EmitGet(m);	// Instance
                            foreach (var arg in args)
                                arg.EmitGet(m);
                            m.IL.EmitCall(OpCodes.Call, methodType, null);
                        }
                        else	// Invoke as delegate
                        {

                            function.EmitGet(m);
                            foreach (var arg in args)
                                arg.EmitGet(m);
                            var delegateType = function.Type.GetNative(compiler.Emitter);
                            m.IL.EmitCall(OpCodes.Callvirt, delegateType.GetMethod("Invoke"), null);
                        }
                    }
                );
        }

		public override ExpressionContext CompileCallExpression(Compiler compiler, Frame frame, ExpressionContext function, Parse.CallExpression callExpression, BaseType typeHint)
		{
			// Compile arguments
			var args = new ExpressionContext[callExpression.Arguments.Count];
			for (var i = 0; i < callExpression.Arguments.Count; i++)
				args[i] = compiler.CompileExpression(frame, callExpression.Arguments[i]);

            return CompileCallExpression(compiler, frame, function, callExpression, typeHint, args);			
		}

		protected override void EmitBinaryOperator(MethodContext method, Compiler compiler, ExpressionContext left, ExpressionContext right, Parse.BinaryExpression expression)
		{
			switch (expression.Operator)
			{
				default: throw NotSupported(expression);
			}
		}

		protected override void EmitUnaryOperator(MethodContext method, Compiler compiler, ExpressionContext inner, Parse.UnaryExpression expression)
		{
			switch (expression.Operator)
			{
				default: throw NotSupported(expression);
			}
		}

		public override System.Type GetNative(Compile.Emitter emitter)
		{
			return emitter.FindOrCreateNativeFromFunctionType(this);
		}

		public static FunctionType FromMethod(MethodInfo method, Emitter emitter)
		{
			return 
				new FunctionType
				{
					Parameters = 
					(
						from p in method.GetParameters() 
						select new FunctionParameter { Name = Name.FromNative(p.Name), Type = emitter.TypeFromNative(p.ParameterType) }
					).ToList(),
					Type = emitter.TypeFromNative(method.ReturnType)
				};
		}

		public override int GetHashCode()
		{
			var running = 0;
			foreach (var p in _parameters)
				running = running * 83 + p.Name.GetHashCode() * 83 + p.Type.GetHashCode();
			return running * 83 + Type.GetHashCode();
		}

		public override bool Equals(object obj)
		{
			if (obj is FunctionType)
				return (FunctionType)obj == this;
			else
				return base.Equals(obj);
		}

		public static bool operator ==(FunctionType left, FunctionType right)
		{
			return Object.ReferenceEquals(left, right) 
				|| 
				(
					!Object.ReferenceEquals(right, null) 
						&& !Object.ReferenceEquals(left, null)
						&& left.GetType() == right.GetType() 
						&& left.Parameters.SequenceEqual(right.Parameters) 
						&& left.Type == right.Type
				);
		}

		public static bool operator !=(FunctionType left, FunctionType right)
		{
			return !(left == right);
		}

		public override Parse.Expression BuildDefault()
		{
			return
				new Parse.FunctionSelector 
				{ 
					Expression = new Parse.ClausedExpression { Expression = Type.BuildDefault() },
					Parameters =
					(
						from p in Parameters 
						select new Parse.FunctionParameter { Name = p.Name.ToID(), Type = p.Type.BuildDOM() }
					).ToList()
				};
		}

		public override Parse.TypeDeclaration BuildDOM()
		{
			return 
				new Parse.FunctionType 
				{ 
					ReturnType = Type.BuildDOM(),
					Parameters = 
					(
						from p in Parameters
						select new Parse.FunctionParameter { Name = p.Name.ToID(), Type = p.Type.BuildDOM() }
					).ToList()
					// TODO: type parameters
					// TypeParameters =
				};
		}

		public override ExpressionContext Convert(ExpressionContext expression, BaseType target)
		{
			// Allow a function type to be converted if the argument types and the return type are the same

			var source = (FunctionType)expression.Type;
			FunctionType targetFunction;
			// Validate that the target is a function and has the same number of parameters and the same return type
			if 
			(
				!(target is FunctionType) 
					|| source.Parameters.Count != (targetFunction = (FunctionType)target).Parameters.Count 
					|| source.Type != targetFunction.Type
			)
				return base.Convert(expression, target);
			
			// Validate the parameter types
			for (var i = 0; i < source.Parameters.Count; i++)
				if (source.Parameters[i].Type != targetFunction.Parameters[i].Type)
					return base.Convert(expression, target);

			var result = expression.Clone();
			result.Type = target;
			return result;
		}
	}
}
