using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace MockGenereator
{
	internal static class Utilities
	{
		public static bool HasInputGetter(this IPropertySymbol p)
		{
			return p.GetMethod != null && p.GetMethod.HasAttribute("MockGenerator", "InputAttribute");
		}

		public static bool HasOutputGetter(this IPropertySymbol p)
		{
			return p.GetMethod != null && p.GetMethod.HasAttribute("MockGenerator", "OutputAttribute");
		}

		public static bool IsTracked(this IPropertySymbol p)
		{
			return p.HasInputGetter() || p.HasOutputGetter()
			|| (p.SetMethod != null && p.SetMethod.HasAttribute("MockGenerator", "OutputAttribute"));
		}

		public static string ResolveMockTypeName(this ITypeSymbol fieldType)
		{
			if (fieldType is INamedTypeSymbol named &&
				named.HasAttribute("MockGenerator", "GenerateMockViewAttribute"))
			{
				var ns = named.ContainingNamespace.IsGlobalNamespace ? "" : named.ContainingNamespace + ".";
				return $"MockView.{ns}Mock{named.Name}{named.TypeArguments.GenericArgs()}";
			}
			return null;
		}

		public enum ResolveStatus { Found, NotFound, Ambiguous, AsNotImplemented, AsMissingAttribute }

		public struct ResolveResult
		{
			public ResolveStatus Status;
			public string Name;
			public int MatchCount;
			public ITypeSymbol AsTarget;
			public string RequiredAttribute;
		}

		public static string ResolveViewInterfaceName(this ITypeSymbol fieldType, bool input, bool output)
			=> ResolveViewInterface(fieldType, input, output, null).Name;

		public static ResolveResult ResolveViewInterface(this ITypeSymbol fieldType, bool input, bool output, ISymbol attributeHolder)
		{
			// 1. If As is specified on a field attribute, validate and use it.
			if (attributeHolder is IFieldSymbol)
			{
				var asType = GetAsTarget(attributeHolder, input, output);
				if (asType != null)
				{
					var implemented = fieldType.AllInterfaces.Any(x =>
						SymbolEqualityComparer.Default.Equals(x, asType))
						|| SymbolEqualityComparer.Default.Equals(fieldType, asType);
					if (!implemented)
					{
						return new ResolveResult { Status = ResolveStatus.AsNotImplemented, AsTarget = asType };
					}
					bool hasInput = asType.HasAttribute("MockGenerator", "InputAttribute");
					bool hasOutput = asType.HasAttribute("MockGenerator", "OutputAttribute");
					if (input && !hasInput)
					{
						return new ResolveResult { Status = ResolveStatus.AsMissingAttribute, AsTarget = asType, RequiredAttribute = "InputAttribute" };
					}
					if (output && !hasOutput)
					{
						return new ResolveResult { Status = ResolveStatus.AsMissingAttribute, AsTarget = asType, RequiredAttribute = "OutputAttribute" };
					}
					return new ResolveResult { Status = ResolveStatus.Found, Name = asType.QualifiedName() };
				}
			}

			var matches = fieldType.AllInterfaces.Where(x =>
				(!input || x.HasAttribute("MockGenerator", "InputAttribute")) &&
				(!output || x.HasAttribute("MockGenerator", "OutputAttribute"))).ToList();

			if (matches.Count > 1)
			{
				return new ResolveResult { Status = ResolveStatus.Ambiguous, MatchCount = matches.Count };
			}
			if (matches.Count == 1)
			{
				return new ResolveResult { Status = ResolveStatus.Found, Name = matches[0].QualifiedName() };
			}

			if (fieldType is INamedTypeSymbol named &&
				named.HasAttribute("MockGenerator", "GenerateViewInterfacesAttribute"))
			{
				var ns = named.ContainingNamespace.IsGlobalNamespace ? "" : named.ContainingNamespace + ".";
				var suffix = (input && output) ? "" : (input ? "Input" : "Output");
				return new ResolveResult { Status = ResolveStatus.Found, Name = $"MockView.{ns}I{named.Name}{suffix}{named.TypeArguments.GenericArgs()}" };
			}

			return new ResolveResult { Status = ResolveStatus.NotFound };
		}

		static INamedTypeSymbol GetAsTarget(ISymbol symbol, bool input, bool output)
		{
			foreach (var attr in symbol.GetAttributes())
			{
				var ac = attr.AttributeClass;
				if (ac == null) continue;
				if (ac.ContainingNamespace?.ToString() != "MockGenerator") continue;
				if (input && ac.MetadataName == "InputAttribute")
				{
					var v = FindNamedArg(attr, "As");
					if (v != null) return v;
				}
				if (output && ac.MetadataName == "OutputAttribute")
				{
					var v = FindNamedArg(attr, "As");
					if (v != null) return v;
				}
			}
			return null;
		}

		static INamedTypeSymbol FindNamedArg(AttributeData attr, string name)
		{
			foreach (var kv in attr.NamedArguments)
			{
				if (kv.Key == name && !kv.Value.IsNull && kv.Value.Value is INamedTypeSymbol t)
				{
					return t;
				}
			}
			return null;
		}

		public static bool HasAsArgument(ISymbol symbol)
		{
			foreach (var attr in symbol.GetAttributes())
			{
				var ac = attr.AttributeClass;
				if (ac == null) continue;
				if (ac.ContainingNamespace?.ToString() != "MockGenerator") continue;
				if (ac.MetadataName != "InputAttribute" && ac.MetadataName != "OutputAttribute") continue;
				if (FindNamedArg(attr, "As") != null) return true;
			}
			return false;
		}

		/// <summary>
		/// Collect [Input] / [Output] interfaces that contribute members to the Mock.
		/// Returns the directly-implemented attributed interfaces of <paramref name="view"/>, plus all of
		/// their (possibly untagged) base interfaces. For each interface in the result, members declared
		/// on that interface (not on its bases) contribute to the Mock; declaring interface is used for
		/// prefix naming when there are multiple sources.
		/// </summary>
		public static List<INamedTypeSymbol> CollectAttributedInterfaces(INamedTypeSymbol view, bool input, bool output)
		{
			var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
			var result = new List<INamedTypeSymbol>();

			foreach (var iface in view.Interfaces)
			{
				if (!IsAttributed(iface, input, output)) continue;
				AddRecursive(iface, seen, result);
			}
			return result;
		}

		/// <summary>
		/// Directly-implemented attributed interfaces only (used for Mock's implements list and umbrella inheritance).
		/// </summary>
		public static List<INamedTypeSymbol> DirectAttributedInterfaces(INamedTypeSymbol view, bool input, bool output)
		{
			var result = new List<INamedTypeSymbol>();
			foreach (var iface in view.Interfaces)
			{
				if (IsAttributed(iface, input, output))
				{
					result.Add(iface);
				}
			}
			return result;
		}

		static bool IsAttributed(INamedTypeSymbol iface, bool input, bool output)
		{
			if (input && !iface.HasAttribute("MockGenerator", "InputAttribute")) return false;
			if (output && !iface.HasAttribute("MockGenerator", "OutputAttribute")) return false;
			return input || output;
		}

		static void AddRecursive(INamedTypeSymbol iface, HashSet<INamedTypeSymbol> seen, List<INamedTypeSymbol> result)
		{
			if (!seen.Add(iface)) return;
			result.Add(iface);
			foreach (var b in iface.Interfaces)
			{
				AddRecursive(b, seen, result);
			}
		}

		/// <summary>
		/// Simple name of an interface, used as prefix in slot names (e.g., "IFoo_OnA").
		/// Generic type arguments are intentionally omitted (see memory project_multi_interface_mock_design §"命名 mangling の限界").
		/// </summary>
		public static string PrefixName(this INamedTypeSymbol iface) => iface.Name;

		public static bool HasAttribute<T>(this T self, string @namespace, string name) where T : ISymbol
		{
			foreach (var attr in self.GetAttributes())
			{
				var attrClass = attr.AttributeClass;
				if (attrClass.ContainingNamespace.ToString() == @namespace && attrClass.MetadataName == name)
				{
					return true;
				}
			}
			return false;
		}

		public static string QualifiedName(this ITypeSymbol symbol)
		{
			switch (symbol)
			{
				case IArrayTypeSymbol array:
					return array.QualifiedName();
				case INamedTypeSymbol named:
					return named.QualifiedName();
				case ITypeParameterSymbol typeParameter:
					return typeParameter.QualifiedName();
			}
			return $"not implemented: {symbol.GetType()}";
		}

		public static string QualifiedName(this ITypeParameterSymbol symbol)
		{
			return symbol.Name;
		}

		public static string QualifiedName(this IArrayTypeSymbol symbol)
		{
			return symbol.ElementType.QualifiedName() + $"[{string.Join("", Enumerable.Repeat(",", symbol.Rank - 1))}]";
		}

		public static string QualifiedName(this INamedTypeSymbol symbol)
		{
			using var _ = StringBuilderHolder.Get(out var sb);

			sb.Append("global::");
			if (!symbol.ContainingNamespace.IsGlobalNamespace)
			{
				sb.Append(symbol.ContainingNamespace);
				sb.Append('.');
			}

			var containingType = symbol.ContainingType;
			while (containingType != null)
			{
				sb.Append(containingType.Name);
				sb.Append(containingType.TypeArguments.GenericArgs());
				sb.Append('.');
				containingType = containingType.ContainingType;
			}

			var name = symbol.Name;
			sb.Append(name);
			sb.Append(symbol.TypeArguments.GenericArgs());

			return sb.ToString();
		}

		public static string GenericArgs(this ImmutableArray<ITypeSymbol> typeArguments)
		{
			if (typeArguments.Length == 0)
			{
				return "";
			}

			using var _ = StringBuilderHolder.Get(out var sb);
			sb.Append('<');
			for (var i = 0; i < typeArguments.Length; ++i)
			{
				if (i != 0)
				{
					sb.Append(", ");
				}
				if (typeArguments[i] is INamedTypeSymbol named)
				{
					sb.Append(named.QualifiedName());
				}
				else
				{
					sb.Append(typeArguments[i].QualifiedName());
				}
			}
			sb.Append('>');
			return sb.ToString();
		}

		public static string ReturnTypeName(this IMethodSymbol symbol)
		{
			if (symbol.ReturnsVoid)
			{
				return "void";
			}
			return symbol.ReturnType.QualifiedName();
		}

		public static string ToPropertyName(this IFieldSymbol field)
		{
			var name = field.Name;
			if (name.StartsWith("_"))
			{
				return name.Substring(1);
			}
			else
			{
				return char.ToUpper(name[0]) + name.Substring(1);
			}
		}

		public static string GenericsParams(this ImmutableArray<ITypeParameterSymbol> typeParams)
		{
			if (typeParams.Length != 0)
			{
				using var _ = StringBuilderHolder.Get(out var sb);
				sb.Append('<');
				for (var i = 0; i < typeParams.Length; ++i)
				{
					if (i != 0)
					{
						sb.Append(", ");
					}
					sb.Append(typeParams[i].Name);
				}
				sb.Append('>');
				return sb.ToString();
			}
			else
			{
				return "";
			}
		}

		public static string GenericsConstraints(this ImmutableArray<ITypeParameterSymbol> typeParams)
		{
			if (typeParams.Length != 0)
			{
				using var _ = StringBuilderHolder.Get(out var result);
				using var __ = StringBuilderHolder.Get(out var sb);
				foreach (var param in typeParams)
				{
					sb.Clear();

					var t = param.ConstraintTypes;
					if (param.HasNotNullConstraint)
					{
						if (sb.Length > 0)
						{
							sb.Append(',');
						}
						sb.Append(" notnull");
					}
					if (param.HasReferenceTypeConstraint)
					{
						if (sb.Length > 0)
						{
							sb.Append(',');
						}
						sb.Append(" class");
					}
					if (param.HasUnmanagedTypeConstraint)
					{
						if (sb.Length > 0)
						{
							sb.Append(',');
						}
						sb.Append(" unmanaged");
					}
					if (param.HasValueTypeConstraint)
					{
						if (sb.Length > 0)
						{
							sb.Append(", ");
						}
						sb.Append(" struct");
					}
					if (param.HasConstructorConstraint)
					{
						if (sb.Length > 0)
						{
							sb.Append(',');
						}
						sb.Append(" new()");
					}
					if (t.Length != 0)
					{
						for (var j = 0; j < t.Length; ++j)
						{
							if (sb.Length > 0)
							{
								sb.Append(',');
							}
							sb.Append(' ');
							sb.Append(t[j].QualifiedName());
						}
					}
					if (sb.Length > 0)
					{
						result.Append($" where {param.Name} :");
						result.Append(sb.ToString());
					}
				}

				if (result.Length > 0)
				{
					return result.ToString();
				}
			}
			return "";
		}

		public static string RefKindModifier(this RefKind kind)
		{
			switch (kind)
			{
				case RefKind.Ref: return "ref ";
				case RefKind.Out: return "out ";
				case RefKind.In: return "in ";
				default: return "";
			}
		}

		public static string MethodParams(this IMethodSymbol symbol, bool withDefaults = false)
		{
			using var _ = StringBuilderHolder.Get(out var sb);
			sb.Append('(');
			var ps = symbol.Parameters;
			for (var i = 0; i < ps.Length; ++i)
			{
				var p = ps[i];
				if (i != 0)
				{
					sb.Append(", ");
				}
				if (withDefaults && p.HasExplicitDefaultValue)
				{
					sb.Append($"{p.RefKind.RefKindModifier()}{p.Type.QualifiedName()} {p.Name} = {FormatDefault(p.ExplicitDefaultValue, p.Type)}");
				}
				else
				{
					sb.Append($"{p.RefKind.RefKindModifier()}{p.Type.QualifiedName()} {p.Name}");
				}
			}
			sb.Append(')');
			return sb.ToString();
		}

		public static string MethodArgs(this IMethodSymbol symbol)
		{
			using var _ = StringBuilderHolder.Get(out var sb);
			sb.Append('(');
			var ps = symbol.Parameters;
			for (var i = 0; i < ps.Length; ++i)
			{
				var p = ps[i];
				if (i != 0)
				{
					sb.Append(", ");
				}
				sb.Append($"{p.RefKind.RefKindModifier()}{p.Name}");
			}
			sb.Append(')');
			return sb.ToString();
		}

		static string FormatDefault(object value, ITypeSymbol type)
		{
			if (value == null) return type.IsReferenceType ? "null" : "default";
			switch (value)
			{
				case bool b: return b ? "true" : "false";
				case string s: return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
				case char c: return "'" + (c == '\'' ? "\\'" : c.ToString()) + "'";
				case float f: return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f";
				case double d: return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d";
				case decimal m: return m.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m";
				default:
					if (type.TypeKind == TypeKind.Enum)
					{
						return "(" + type.QualifiedName() + ")" + System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
					}
					return System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
			}
		}

		/// <summary>
		/// Emits a mock event member: the event itself plus a RaiseXxx helper that is null-safe
		/// and handles ref/out/in parameters.
		/// </summary>
		public static void EmitMockEventMember(this StringBuilder sb, string i, IEventSymbol @event)
		{
			var name = @event.Name;
			var eventType = @event.Type.QualifiedName();
			var invoke = (@event.Type as INamedTypeSymbol)?.DelegateInvokeMethod;

			if (invoke == null)
			{
				sb.Append($$"""

					{{i}}public event {{eventType}} {{name}};
					""");
				return;
			}

			var ret = invoke.ReturnTypeName();
			var returnsVoid = invoke.ReturnsVoid;
			var paramsText = invoke.MethodParams();
			var argsText = invoke.MethodArgs();
			var hasOut = invoke.Parameters.Any(p => p.RefKind == RefKind.Out);

			string body;
			if (hasOut)
			{
				var outInit = string.Concat(invoke.Parameters
					.Where(p => p.RefKind == RefKind.Out)
					.Select(p => $"\n{i}\t{p.Name} = default;"));
				body = returnsVoid
					? $"\n{i}{{\n{i}\tif ({name} != null) {{ {name}.Invoke{argsText}; return; }}{outInit}\n{i}}}"
					: $"\n{i}{{\n{i}\tif ({name} != null) return {name}.Invoke{argsText};{outInit}\n{i}\treturn default({ret});\n{i}}}";
			}
			else
			{
				body = returnsVoid
					? $" => {name}?.Invoke{argsText};"
					: $" => {name} != null ? {name}.Invoke{argsText} : default({ret});";
			}

			sb.Append($$"""

				{{i}}public event {{eventType}} {{name}};
				{{i}}public {{ret}} Raise{{name}}{{paramsText}}{{body}}
				""");
		}

		/// <summary>
		/// Emits a mock method member: a custom delegate (non-generic) or nested interface (generic),
		/// a XxxFunc holder property, and a forwarding method that returns default when the Func is unset.
		/// </summary>
		public static void EmitMockMethodMember(this StringBuilder sb, string i, IMethodSymbol method)
		{
			var name = method.Name;
			var funcName = name + "Func";
			var delegateName = name + "Delegate";
			var ret = method.ReturnTypeName();
			var returnsVoid = method.ReturnsVoid;
			var isGeneric = method.IsGenericMethod;
			var generics = method.TypeParameters.GenericsParams();
			var constraints = method.TypeParameters.GenericsConstraints();
			var paramsText = method.MethodParams();
			var paramsTextWD = method.MethodParams(withDefaults: true);
			var argsText = method.MethodArgs();
			var hasOut = method.Parameters.Any(p => p.RefKind == RefKind.Out);
			var invoke = isGeneric ? "Call" + generics : "Invoke";

			if (isGeneric)
			{
				delegateName = "I" + delegateName;
			}

			var typeDecl = isGeneric
				? $$"""
					{{i}}public interface {{delegateName}}
					{{i}}{
					{{i}}	{{ret}} Call{{generics}}{{paramsText}}{{constraints}};
					{{i}}}
					"""
				: $"{i}public delegate {ret} {delegateName}{paramsText};";

			string body;
			if (hasOut)
			{
				var outInit = string.Concat(method.Parameters
					.Where(p => p.RefKind == RefKind.Out)
					.Select(p => $"\n{i}\t{p.Name} = default;"));
				body = returnsVoid
					? $"\n{i}{{\n{i}\tif ({funcName} != null) {{ {funcName}.{invoke}{argsText}; return; }}{outInit}\n{i}}}"
					: $"\n{i}{{\n{i}\tif ({funcName} != null) return {funcName}.{invoke}{argsText};{outInit}\n{i}\treturn default({ret});\n{i}}}";
			}
			else
			{
				body = returnsVoid
					? $" => {funcName}?.{invoke}{argsText};"
					: $" => {funcName} != null ? {funcName}.{invoke}{argsText} : default({ret});";
			}

			sb.Append($$"""

				{{typeDecl}}
				{{i}}public {{delegateName}} {{funcName}} { get; set; }
				{{i}}public {{ret}} {{name}}{{generics}}{{paramsTextWD}}{{constraints}}{{body}}
				""");
		}

		/// <summary>
		/// convert to a string that can be used as the value of a document comment cref
		/// </summary>
		public static string ToCref(this INamedTypeSymbol symbol)
		{
			using var _ = StringBuilderHolder.Get(out var sb);
			var ns = symbol.ContainingNamespace;
			if (ns.IsGlobalNamespace)
			{
				sb.Append("global::");
			}
			else
			{
				sb.Append("global::");
				sb.Append(ns);
				sb.Append('.');
			}
			sb.Append(symbol.Name);
			sb.Append(symbol.TypeParameters.GenericsParams().Replace('<', '{').Replace('>', '}'));
			return sb.ToString();
		}
	}
}
