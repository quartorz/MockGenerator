using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace MockGenerator
{
	internal static class Utilities
	{
		public const string AttributeNamespace = "MockGenerator";

		// 生成される 2 面の語彙。値を変えれば属性名も生成 interface 名もまとめて変わる。
		// UserSide = 画面から入ってくるもの（ユーザー操作）、AppSide = 画面へ出ていくもの（表示の書き換え）。
		public const string UserSideName = "Input";
		public const string AppSideName = "Output";
		public const string UserSideAttributeName = UserSideName + "Attribute";
		public const string AppSideAttributeName = AppSideName + "Attribute";

		/// <summary>
		/// First lines of every generated file: enables nullable annotations so the <c>?</c> the generators
		/// emit is valid whatever the consuming assembly's nullable setting is.
		/// </summary>
		public const string GeneratedFileHeader = "#nullable enable\n\n";

		/// <summary>Appends <see cref="GeneratedFileHeader"/>. Call once, before any other output.</summary>
		public static StringBuilder AppendGeneratedHeader(this StringBuilder sb) => sb.Append(GeneratedFileHeader);

		public static bool HasInputGetter(this IPropertySymbol p)
		{
			return p.GetMethod != null && p.GetMethod.HasAttribute(AttributeNamespace, UserSideAttributeName);
		}

		public static bool HasOutputGetter(this IPropertySymbol p)
		{
			return p.GetMethod != null && p.GetMethod.HasAttribute(AttributeNamespace, AppSideAttributeName);
		}

		public static bool IsTracked(this IPropertySymbol p)
		{
			return p.HasInputGetter() || p.HasOutputGetter()
			|| (p.SetMethod != null && p.SetMethod.HasAttribute(AttributeNamespace, AppSideAttributeName));
		}

		public static string ResolveMockTypeName(this ITypeSymbol fieldType)
		{
			if (fieldType is INamedTypeSymbol named &&
				named.HasAttribute(AttributeNamespace, "GenerateMockViewAttribute"))
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
			string WithFieldNullability(string name) =>
				fieldType.NullableAnnotation == NullableAnnotation.Annotated ? name + "?" : name;

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
					bool hasInput = asType.HasAttribute(AttributeNamespace, UserSideAttributeName);
					bool hasOutput = asType.HasAttribute(AttributeNamespace, AppSideAttributeName);
					if (input && !hasInput)
					{
						return new ResolveResult { Status = ResolveStatus.AsMissingAttribute, AsTarget = asType, RequiredAttribute = UserSideAttributeName };
					}
					if (output && !hasOutput)
					{
						return new ResolveResult { Status = ResolveStatus.AsMissingAttribute, AsTarget = asType, RequiredAttribute = AppSideAttributeName };
					}
					return new ResolveResult { Status = ResolveStatus.Found, Name = WithFieldNullability(asType.QualifiedName()) };
				}
			}

			var matches = fieldType.AllInterfaces.Where(x =>
				(!input || x.HasAttribute(AttributeNamespace, UserSideAttributeName)) &&
				(!output || x.HasAttribute(AttributeNamespace, AppSideAttributeName))).ToList();

			if (matches.Count > 1)
			{
				return new ResolveResult { Status = ResolveStatus.Ambiguous, MatchCount = matches.Count };
			}
			if (matches.Count == 1)
			{
				return new ResolveResult { Status = ResolveStatus.Found, Name = WithFieldNullability(matches[0].QualifiedName()) };
			}

			if (fieldType is INamedTypeSymbol named &&
				named.HasAttribute(AttributeNamespace, "GenerateViewInterfacesAttribute"))
			{
				var ns = named.ContainingNamespace.IsGlobalNamespace ? "Generated." : named.ContainingNamespace + ".Generated.";
				var suffix = (input && output) ? "" : (input ? UserSideName : AppSideName);
				return new ResolveResult { Status = ResolveStatus.Found, Name = WithFieldNullability($"global::{ns}I{named.Name}{suffix}{named.TypeArguments.GenericArgs()}") };
			}

			return new ResolveResult { Status = ResolveStatus.NotFound };
		}

		static INamedTypeSymbol GetAsTarget(ISymbol symbol, bool input, bool output)
		{
			foreach (var attr in symbol.GetAttributes())
			{
				var ac = attr.AttributeClass;
				if (ac == null) continue;
				if (ac.ContainingNamespace?.ToString() != AttributeNamespace) continue;
				if (input && ac.MetadataName == UserSideAttributeName)
				{
					var v = FindNamedArg(attr, "As");
					if (v != null) return v;
				}
				if (output && ac.MetadataName == AppSideAttributeName)
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
				if (ac.ContainingNamespace?.ToString() != AttributeNamespace) continue;
				if (ac.MetadataName != UserSideAttributeName && ac.MetadataName != AppSideAttributeName) continue;
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

			foreach (var iface in SelfAndBaseClassInterfaces(view))
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
			var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
			var result = new List<INamedTypeSymbol>();
			foreach (var iface in SelfAndBaseClassInterfaces(view))
			{
				if (IsAttributed(iface, input, output) && seen.Add(iface))
				{
					result.Add(iface);
				}
			}
			return result;
		}

		/// <summary>
		/// Interfaces directly implemented by <paramref name="view"/> or any of its base classes, deduped
		/// in most-derived-first order. <c>view.Interfaces</c> only sees this type's own base list; this
		/// walks the base-class chain so interfaces implemented by a base class are also considered.
		/// (Base interfaces of those interfaces are added separately by <see cref="AddRecursive"/>.)
		/// </summary>
		static IEnumerable<INamedTypeSymbol> SelfAndBaseClassInterfaces(INamedTypeSymbol view)
		{
			var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
			for (var t = view; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
			{
				foreach (var iface in t.Interfaces)
				{
					if (seen.Add(iface)) yield return iface;
				}
			}
		}

		/// <summary>
		/// Collects the members a <c>[GenerateInterface]</c> class contributes to its generated interface:
		/// public, non-static, non-indexer properties (with a public accessor), ordinary methods (excluding
		/// <c>object</c> overrides), and events — gathered from the class <em>and its base classes</em> (up to
		/// but excluding <c>object</c>), most-derived first, deduped so an override or <c>new</c> hides the
		/// inherited member. The class models a (non-view) feature, so its mock must expose the full public
		/// surface, including members inherited from a base class.
		/// </summary>
		public static List<ISymbol> CollectGenerateInterfaceMembers(INamedTypeSymbol typeSymbol)
		{
			var result = new List<ISymbol>();
			var seenMethods = new HashSet<string>();
			var seenProps = new HashSet<string>();
			var seenEvents = new HashSet<string>();
			for (var t = typeSymbol; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
			{
				foreach (var member in t.GetMembers())
				{
					if (member.DeclaredAccessibility != Accessibility.Public) continue;
					if (member.IsStatic) continue;
					switch (member)
					{
						case IPropertySymbol p when !p.IsIndexer:
						{
							var getterPublic = p.GetMethod != null && p.GetMethod.DeclaredAccessibility == Accessibility.Public;
							var setterPublic = p.SetMethod != null && p.SetMethod.DeclaredAccessibility == Accessibility.Public;
							if ((getterPublic || setterPublic) && seenProps.Add(p.Name)) result.Add(p);
							break;
						}
						case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary:
						{
							if (m.OverriddenMethod?.ContainingType?.SpecialType == SpecialType.System_Object) break;
							if (seenMethods.Add(m.Name + "`" + m.Arity + m.MethodParamTypes())) result.Add(m);
							break;
						}
						case IEventSymbol e:
						{
							if (seenEvents.Add(e.Name)) result.Add(e);
							break;
						}
					}
				}
			}
			return result;
		}

		static bool IsAttributed(INamedTypeSymbol iface, bool input, bool output)
		{
			if (input && !iface.HasAttribute(AttributeNamespace, UserSideAttributeName)) return false;
			if (output && !iface.HasAttribute(AttributeNamespace, AppSideAttributeName)) return false;
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
		/// Generic type arguments are intentionally omitted.
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

		/// <summary>
		/// Fully qualified, <c>global::</c>-prefixed spelling of <paramref name="symbol"/>, carrying the
		/// nullable annotation (<c>?</c>) of reference types, arrays and type parameters — recursively through
		/// type arguments and array elements. <c>Nullable&lt;T&gt;</c> keeps its generic spelling.
		/// </summary>
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
			return symbol.Name + symbol.NullableSuffix();
		}

		public static string QualifiedName(this IArrayTypeSymbol symbol)
		{
			return symbol.ElementType.QualifiedName() + $"[{string.Join("", Enumerable.Repeat(",", symbol.Rank - 1))}]" + symbol.NullableSuffix();
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
			sb.Append(symbol.NullableSuffix());

			return sb.ToString();
		}

		/// <summary>
		/// <c>?</c> when <paramref name="symbol"/> is annotated nullable and is not a value type (a value
		/// type's <c>?</c> is already spelled as <c>Nullable&lt;T&gt;</c>), otherwise empty.
		/// </summary>
		static string NullableSuffix(this ITypeSymbol symbol)
		{
			return symbol.NullableAnnotation == NullableAnnotation.Annotated && !symbol.IsValueType ? "?" : "";
		}

		/// <summary>
		/// <see cref="QualifiedName(ITypeSymbol)"/> with the type forced to nullable. Used for mock slots that
		/// are legitimately unset until the test assigns them.
		/// </summary>
		public static string NullableQualifiedName(this ITypeSymbol symbol)
		{
			return symbol.WithNullableAnnotation(NullableAnnotation.Annotated).QualifiedName();
		}

		/// <summary>
		/// True when <c>default</c> of <paramref name="symbol"/> is a null that the declared type does not admit:
		/// a non-nullable reference type or a type parameter not known to be a value type. Such spots need
		/// <c>!</c> because the mock deliberately returns / stores "unset" there.
		/// </summary>
		public static bool DefaultIsForbiddenNull(this ITypeSymbol symbol)
		{
			return !symbol.IsValueType && symbol.NullableAnnotation != NullableAnnotation.Annotated;
		}

		/// <summary>
		/// <c> = default!;</c> after the accessor list of an auto-property whose type's default is a forbidden
		/// null (see <see cref="DefaultIsForbiddenNull"/>), otherwise empty.
		/// </summary>
		public static string AutoPropertyInitializer(this ITypeSymbol symbol)
		{
			return symbol.DefaultIsForbiddenNull() ? " = default!;" : "";
		}

		/// <summary>
		/// <c>default(T)</c> expression returned by a forwarder whose slot is unset, with <c>!</c> when that
		/// default is a forbidden null (see <see cref="DefaultIsForbiddenNull"/>) that no
		/// <c>[return: MaybeNull]</c> admits.
		/// </summary>
		public static string ReturnDefaultExpression(this IMethodSymbol method)
		{
			var forbidden = method.ReturnType.DefaultIsForbiddenNull()
				&& !method.GetReturnTypeAttributes().Any(a => a.AttributeClass?.ContainingNamespace?.ToString() == NullableAttributeNamespace && a.AttributeClass.MetadataName == "MaybeNullAttribute");
			return $"default({method.ReturnType.QualifiedName()})" + (forbidden ? "!" : "");
		}

		/// <summary>
		/// <c>name = default;</c> initialisation of an <c>out</c> parameter, with <c>!</c> when that default is a
		/// forbidden null (see <see cref="DefaultIsForbiddenNull"/>).
		/// </summary>
		public static string OutDefaultAssignment(this IParameterSymbol p)
		{
			return $"{p.Name} = default{(p.Type.DefaultIsForbiddenNull() ? "!" : "")};";
		}

		const string NullableAttributeNamespace = "System.Diagnostics.CodeAnalysis";

		static readonly HashSet<string> NullableAttributeNames = new HashSet<string>
		{
			"AllowNullAttribute",
			"DisallowNullAttribute",
			"MaybeNullAttribute",
			"NotNullAttribute",
			"MaybeNullWhenAttribute",
			"NotNullWhenAttribute",
			"NotNullIfNotNullAttribute",
		};

		/// <summary>
		/// Renders the nullable-analysis attributes of <c>System.Diagnostics.CodeAnalysis</c> found in
		/// <paramref name="attributes"/> as <c>[global::...Name(args)] </c> (one bracket per attribute, each
		/// followed by a space), or empty when none apply. Constructor arguments are the <c>bool</c> / <c>string</c>
		/// values those attributes take.
		/// </summary>
		/// <param name="attributes">The attributes to filter.</param>
		/// <param name="target">An attribute target such as <c>return</c>, or null for the default target.</param>
		public static string NullableAttributes(this ImmutableArray<AttributeData> attributes, string target = null)
		{
			if (attributes.Length == 0) return "";
			using var _ = StringBuilderHolder.Get(out var sb);
			foreach (var attr in attributes)
			{
				var ac = attr.AttributeClass;
				if (ac == null) continue;
				if (ac.ContainingNamespace?.ToString() != NullableAttributeNamespace) continue;
				if (!NullableAttributeNames.Contains(ac.MetadataName)) continue;
				sb.Append('[');
				if (target != null)
				{
					sb.Append(target);
					sb.Append(": ");
				}
				sb.Append("global::");
				sb.Append(NullableAttributeNamespace);
				sb.Append('.');
				sb.Append(ac.Name, 0, ac.Name.Length - "Attribute".Length);
				if (attr.ConstructorArguments.Length > 0)
				{
					sb.Append('(');
					for (var i = 0; i < attr.ConstructorArguments.Length; ++i)
					{
						if (i != 0) sb.Append(", ");
						sb.Append(FormatDefault(attr.ConstructorArguments[i].Value, attr.ConstructorArguments[i].Type));
					}
					sb.Append(')');
				}
				sb.Append("] ");
			}
			return sb.ToString();
		}

		/// <summary>Nullable-analysis attributes declared on <paramref name="symbol"/> itself (parameter, property).</summary>
		public static string NullableAttributes(this ISymbol symbol) => symbol.GetAttributes().NullableAttributes();

		static bool HasNullableAttribute(this ISymbol symbol, string metadataName)
		{
			foreach (var attr in symbol.GetAttributes())
			{
				var ac = attr.AttributeClass;
				if (ac != null && ac.ContainingNamespace?.ToString() == NullableAttributeNamespace && ac.MetadataName == metadataName)
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Type of the value the setter of <paramref name="p"/> receives, for the <c>OnXxxSet</c> callback: the
		/// property type, made nullable when <c>[AllowNull]</c> admits a null on input.
		/// </summary>
		public static string SetterValueTypeName(this IPropertySymbol p)
		{
			return p.HasNullableAttribute("AllowNullAttribute") ? p.Type.NullableQualifiedName() : p.Type.QualifiedName();
		}

		/// <summary>
		/// Whether the storage behind <paramref name="p"/> must be seeded with <c>default!</c>: its type's default
		/// is a forbidden null (see <see cref="DefaultIsForbiddenNull"/>), or <c>[NotNull]</c> promises a
		/// non-null value on read, which the mock's unset state cannot honour.
		/// </summary>
		static bool StorageNeedsDefaultInitializer(this IPropertySymbol p)
		{
			return p.Type.DefaultIsForbiddenNull() || p.HasNullableAttribute("NotNullAttribute");
		}

		/// <summary><c> = default!</c> for the backing field of <paramref name="p"/> when its storage needs it, otherwise empty.</summary>
		public static string BackingInitializer(this IPropertySymbol p)
		{
			return p.StorageNeedsDefaultInitializer() ? " = default!" : "";
		}

		/// <summary><see cref="AutoPropertyInitializer"/> for a slot auto-property mirroring <paramref name="p"/>, honouring <c>[NotNull]</c> as well.</summary>
		public static string SlotInitializer(this IPropertySymbol p)
		{
			return p.StorageNeedsDefaultInitializer() ? " = default!;" : "";
		}

		/// <summary>Nullable-analysis attributes on the return value of <paramref name="method"/>, as <c>[return: ...]</c>.</summary>
		public static string ReturnNullableAttributes(this IMethodSymbol method) => method.GetReturnTypeAttributes().NullableAttributes("return");

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
						sb.Append(param.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? " class?" : " class");
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

		/// <summary>
		/// The <c>where T : default</c> clauses an explicit interface implementation of <paramref name="method"/>
		/// needs. An explicit implementation must not restate the interface's constraints (CS0460), yet without
		/// them a <c>T?</c> on an otherwise unconstrained <c>T</c> is read as <c>Nullable&lt;T&gt;</c>;
		/// <c>default</c> states that it is the nullable-reference annotation instead. Emitted only for type
		/// parameters that have no <c>class</c> / <c>struct</c> / <c>notnull</c> / <c>unmanaged</c> constraint,
		/// no class-type constraint, and that appear annotated somewhere in the signature.
		/// </summary>
		public static string GenericsDefaultConstraints(this IMethodSymbol method)
		{
			using var _ = StringBuilderHolder.Get(out var sb);
			foreach (var param in method.TypeParameters)
			{
				if (param.HasReferenceTypeConstraint || param.HasValueTypeConstraint ||
					param.HasNotNullConstraint || param.HasUnmanagedTypeConstraint)
				{
					continue;
				}
				if (param.ConstraintTypes.Any(t => t.TypeKind == TypeKind.Class))
				{
					continue;
				}
				if (ContainsAnnotated(method.ReturnType, param) ||
					method.Parameters.Any(p => ContainsAnnotated(p.Type, param)))
				{
					sb.Append($" where {param.Name} : default");
				}
			}
			return sb.ToString();
		}

		static bool ContainsAnnotated(ITypeSymbol type, ITypeParameterSymbol param)
		{
			switch (type)
			{
				case ITypeParameterSymbol tp:
					return SymbolEqualityComparer.Default.Equals(tp, param) &&
						tp.NullableAnnotation == NullableAnnotation.Annotated;
				case IArrayTypeSymbol array:
					return ContainsAnnotated(array.ElementType, param);
				case INamedTypeSymbol named:
					return named.TypeArguments.Any(a => ContainsAnnotated(a, param));
				default:
					return false;
			}
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
				var mods = p.IsParams ? "params " : p.RefKind.RefKindModifier();
				var attrs = p.NullableAttributes();
				if (withDefaults && p.HasExplicitDefaultValue)
				{
					sb.Append($"{attrs}{mods}{p.Type.QualifiedName()} {p.Name} = {FormatDefault(p.ExplicitDefaultValue, p.Type)}");
				}
				else
				{
					sb.Append($"{attrs}{mods}{p.Type.QualifiedName()} {p.Name}");
				}
			}
			sb.Append(')');
			return sb.ToString();
		}

		/// <summary>
		/// Signature key for dedup: RefKind + parameter types only, no parameter names and no nullable
		/// annotations. C# signatures ignore both, so override/new with renamed params or a differently
		/// annotated type must collide here.
		/// </summary>
		public static string MethodParamTypes(this IMethodSymbol symbol)
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
				sb.Append($"{p.RefKind.RefKindModifier()}{p.Type.QualifiedName().Replace("?", "")}");
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
		/// Emits a mock property member: a backing field, an OnXxxSet callback hook, and a property
		/// whose setter invokes the callback.
		/// </summary>
		public static void EmitMockPropertyMember(this StringBuilder sb, string i, IPropertySymbol p)
		{
			var rawType = p.Type.QualifiedName();
			var pascal = char.ToUpper(p.Name[0]) + p.Name.Substring(1);
			var camel = char.ToLower(p.Name[0]) + p.Name.Substring(1);
			var backing = "_" + camel + "Backing";
			var onSet = "On" + pascal + "Set";
			var attrs = p.NullableAttributes();
			sb.Append($"\n{i}public System.Action<{p.SetterValueTypeName()}>? {onSet} {{ get; set; }}");
			sb.Append($"\n{i}{attrs}private {rawType} {backing}{p.BackingInitializer()};");
			sb.Append($"\n{i}{attrs}public {rawType} {p.Name} {{ get => {backing}; set {{ {backing} = value; {onSet}?.Invoke(value); }} }}");
		}

		/// <summary>
		/// Emits a mock event member: the event itself plus a RaiseXxx helper that is null-safe
		/// and handles ref/out/in parameters.
		/// </summary>
		public static void EmitMockEventMember(this StringBuilder sb, string i, IEventSymbol @event)
		{
			var name = @event.Name;
			var pascal = char.ToUpper(name[0]) + name.Substring(1);
			var eventType = @event.Type.NullableQualifiedName();
			var invoke = (@event.Type as INamedTypeSymbol)?.DelegateInvokeMethod;

			if (invoke == null)
			{
				sb.Append($$"""

					{{i}}public event {{eventType}} {{name}};
					""");
				return;
			}

			var ret = invoke.ReturnTypeName();
			var retAttrs = invoke.ReturnNullableAttributes();
			var paramsText = invoke.MethodParams();
			var body = RaiserBody(i, invoke, name);

			sb.Append($$"""

				{{i}}public event {{eventType}} {{name}};
				{{i}}{{retAttrs}}public {{ret}} Raise{{pascal}}{{paramsText}}{{body}}
				""");
		}

		/// <summary>
		/// Body of a Raise helper / forwarder: calls <paramref name="invokeExpr"/> on <paramref name="slot"/> when
		/// the slot is set; otherwise initialises every <c>out</c> parameter and returns <c>default</c>.
		/// </summary>
		static string RaiserBody(string i, IMethodSymbol invoke, string slot, string invokeExpr = "Invoke")
		{
			var argsText = invoke.MethodArgs();
			var hasOut = invoke.Parameters.Any(p => p.RefKind == RefKind.Out);
			if (hasOut)
			{
				var outInit = string.Concat(invoke.Parameters
					.Where(p => p.RefKind == RefKind.Out)
					.Select(p => $"\n{i}\t{p.OutDefaultAssignment()}"));
				return invoke.ReturnsVoid
					? $"\n{i}{{\n{i}\tif ({slot} != null) {{ {slot}.{invokeExpr}{argsText}; return; }}{outInit}\n{i}}}"
					: $"\n{i}{{\n{i}\tif ({slot} != null) return {slot}.{invokeExpr}{argsText};{outInit}\n{i}\treturn {invoke.ReturnDefaultExpression()};\n{i}}}";
			}
			return invoke.ReturnsVoid
				? $" => {slot}?.{invokeExpr}{argsText};"
				: $" => {slot} != null ? {slot}.{invokeExpr}{argsText} : {invoke.ReturnDefaultExpression()};";
		}

		/// <summary>
		/// Emits a mock property as a prefixed public slot plus an explicit interface implementation that
		/// forwards to it. Used when the same property name is declared with different types across several
		/// base interfaces, so a single implicit public member cannot satisfy all of them.
		/// </summary>
		public static void EmitMockPropertySlot(this StringBuilder sb, string i, IPropertySymbol p, string slotName, string prefix, string explicitTarget)
		{
			var t = p.Type.QualifiedName();
			var attrs = p.NullableAttributes();
			var hasGet = p.GetMethod != null;
			var hasSet = p.SetMethod != null;
			if (hasSet)
			{
				var camel = char.ToLower(slotName[0]) + slotName.Substring(1);
				var backing = "_" + camel + "Backing";
				var onSet = prefix + "On" + char.ToUpper(p.Name[0]) + p.Name.Substring(1) + "Set";
				sb.Append($"\n{i}public System.Action<{p.SetterValueTypeName()}>? {onSet} {{ get; set; }}");
				sb.Append($"\n{i}{attrs}private {t} {backing}{p.BackingInitializer()};");
				if (hasGet)
					sb.Append($"\n{i}{attrs}public {t} {slotName} {{ get => {backing}; set {{ {backing} = value; {onSet}?.Invoke(value); }} }}");
				else
					sb.Append($"\n{i}{attrs}public {t} {slotName} {{ set {{ {backing} = value; {onSet}?.Invoke(value); }} }}");
			}
			else
			{
				sb.Append($"\n{i}{attrs}public {t} {slotName} {{ get; set; }}{p.SlotInitializer()}");
			}
			if (explicitTarget != null)
			{
				if (hasGet && hasSet)
					sb.Append($"\n{i}{attrs}{t} {explicitTarget}.{p.Name} {{ get => {slotName}; set => {slotName} = value; }}");
				else if (hasGet)
					sb.Append($"\n{i}{attrs}{t} {explicitTarget}.{p.Name} => {slotName};");
				else
					sb.Append($"\n{i}{attrs}{t} {explicitTarget}.{p.Name} {{ set => {slotName} = value; }}");
			}
		}

		/// <summary>
		/// Emits a mock event as a prefixed public slot (with a Raise helper) plus an explicit interface
		/// implementation that forwards add/remove to it. Used when the same event name is declared with
		/// different delegate types across several base interfaces.
		/// <para>
		/// The helper keeps the interface prefix in front, mirroring the accessor form the member would take
		/// if the mock exposed one object per interface: prefix "IDialog_" + "OnClick" -&gt;
		/// "IDialog_RaiseOnClick" (not "RaiseIDialog_OnClick").
		/// </para>
		/// </summary>
		public static void EmitMockEventSlot(this StringBuilder sb, string i, IEventSymbol e, string slotName, string prefix, string explicitTarget)
		{
			var eventType = e.Type.QualifiedName();
			var invoke = (e.Type as INamedTypeSymbol)?.DelegateInvokeMethod;
			sb.Append($"\n{i}public event {e.Type.NullableQualifiedName()} {slotName};");
			if (invoke != null)
			{
				var pascal = char.ToUpper(e.Name[0]) + e.Name.Substring(1);
				sb.Append($"\n{i}{invoke.ReturnNullableAttributes()}public {invoke.ReturnTypeName()} {prefix}Raise{pascal}{invoke.MethodParams()}{RaiserBody(i, invoke, slotName)}");
			}
			if (explicitTarget != null)
			{
				sb.Append($"\n{i}event {eventType} {explicitTarget}.{e.Name} {{ add => {slotName} += value; remove => {slotName} -= value; }}");
			}
		}

		/// <summary>One overload of a method group: the interface member and, for a view mock, the view's implementing method.</summary>
		public readonly struct Overload
		{
			public IMethodSymbol Member { get; init; }
			public IMethodSymbol Implementation { get; init; }
		}

		/// <summary>
		/// Collects ordinary methods into ordered groups keyed by slot name, so overloads
		/// (same slot name, different signatures) end up in one group. Duplicate signatures
		/// (e.g. the same method reached through several base interfaces) are dropped. For a view mock,
		/// overloads are deduplicated by the view's implementing method, so one implementation satisfying
		/// several interface members yields one forwarder.
		/// </summary>
		public sealed class MethodGroups
		{
			readonly List<KeyValuePair<string, List<Overload>>> _groups = new List<KeyValuePair<string, List<Overload>>>();
			readonly Dictionary<string, int> _index = new Dictionary<string, int>();
			readonly Dictionary<string, string> _explicitTargets = new Dictionary<string, string>();
			readonly HashSet<string> _seenSig = new HashSet<string>();
			readonly Dictionary<string, HashSet<ISymbol>> _seenImpl = new Dictionary<string, HashSet<ISymbol>>();

			/// <summary>Adds an overload to the group named <paramref name="slotName"/>. Returns false if its signature (or its implementation) was already present.</summary>
			public bool Add(string slotName, IMethodSymbol method, string explicitTarget = null, IMethodSymbol implementation = null)
			{
				if (implementation != null)
				{
					if (!_seenImpl.TryGetValue(slotName, out var impls))
					{
						impls = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
						_seenImpl[slotName] = impls;
					}
					if (!impls.Add(implementation)) return false;
				}
				else if (!_seenSig.Add(slotName + "`" + method.Arity + method.MethodParamTypes()))
				{
					return false;
				}
				if (!_index.TryGetValue(slotName, out var idx))
				{
					idx = _groups.Count;
					_groups.Add(new KeyValuePair<string, List<Overload>>(slotName, new List<Overload>()));
					_index[slotName] = idx;
					if (explicitTarget != null) _explicitTargets[slotName] = explicitTarget;
				}
				_groups[idx].Value.Add(new Overload { Member = method, Implementation = implementation });
				return true;
			}

			public void EmitAll(StringBuilder sb, string i)
			{
				foreach (var g in _groups)
				{
					_explicitTargets.TryGetValue(g.Key, out var target);
					sb.EmitMockMethodGroup(i, g.Key, g.Value, target);
				}
			}

			/// <summary>Invokes <paramref name="action"/> once per group, in insertion order.</summary>
			public void ForEachGroup(System.Action<string, IReadOnlyList<Overload>> action)
			{
				foreach (var g in _groups)
				{
					action(g.Key, g.Value);
				}
			}
		}

		/// <summary>
		/// Emits a mock for one method-name group. A single non-generic method keeps the lambda-assignable
		/// delegate form ({slot}Delegate + {slot}Func). Overload groups (and single generic methods) use a
		/// nested interface I{slot}Delegate carrying one overloaded Call per overload. In every case a
		/// {slot}Func holder, a forwarding method per overload (returns default when the Func is unset), and —
		/// when the slot is renamed or implemented explicitly — explicit interface implementations are emitted.
		/// </summary>
		public static void EmitMockMethodGroup(this StringBuilder sb, string i, string slotName, IReadOnlyList<Overload> overloads, string explicitTarget = null)
		{
			var funcName = slotName + "Func";

			if (overloads.Count == 1 && !(overloads[0].Implementation ?? overloads[0].Member).IsGenericMethod)
			{
				var o = overloads[0];
				var m = o.Implementation ?? o.Member;
				var delegateName = slotName + "Delegate";
				sb.Append($"\n{i}{m.ReturnNullableAttributes()}public delegate {m.ReturnTypeName()} {delegateName}{m.MethodParams()};");
				sb.Append($"\n{i}public {delegateName}? {funcName} {{ get; set; }}");
				EmitForwarder(sb, i, m, o.Member, slotName, funcName, "Invoke", explicitTarget);
				return;
			}

			var ifaceName = "I" + slotName + "Delegate";
			sb.Append($"\n{i}public interface {ifaceName}");
			sb.Append($"\n{i}{{");
			foreach (var o in overloads)
			{
				var m = o.Implementation ?? o.Member;
				var g = m.TypeParameters.GenericsParams();
				var c = m.TypeParameters.GenericsConstraints();
				sb.Append($"\n{i}\t{m.ReturnNullableAttributes()}{m.ReturnTypeName()} Call{g}{m.MethodParams()}{c};");
			}
			sb.Append($"\n{i}}}");
			sb.Append($"\n{i}public {ifaceName}? {funcName} {{ get; set; }}");

			foreach (var o in overloads)
			{
				var m = o.Implementation ?? o.Member;
				EmitForwarder(sb, i, m, o.Member, slotName, funcName, "Call" + m.TypeParameters.GenericsParams(), explicitTarget);
			}
		}

		static void EmitForwarder(StringBuilder sb, string i, IMethodSymbol publicMethod, IMethodSymbol interfaceMember, string slotName, string funcName, string invoke, string explicitTarget)
		{
			var ret = publicMethod.ReturnTypeName();
			var retAttrs = publicMethod.ReturnNullableAttributes();
			var generics = publicMethod.TypeParameters.GenericsParams();
			var constraints = publicMethod.TypeParameters.GenericsConstraints();
			var paramsTextWD = publicMethod.MethodParams(withDefaults: true);
			var body = RaiserBody(i, publicMethod, funcName, invoke);

			sb.Append($"\n{i}{retAttrs}public {ret} {slotName}{generics}{paramsTextWD}{constraints}{body}");

			if (explicitTarget != null)
			{
				// Explicit interface implementations must NOT restate generic constraints (CS0460).
				var memberGenerics = interfaceMember.TypeParameters.GenericsParams();
				sb.Append($"\n{i}{interfaceMember.ReturnNullableAttributes()}{interfaceMember.ReturnTypeName()} {explicitTarget}.{interfaceMember.Name}{memberGenerics}{interfaceMember.MethodParams()}{interfaceMember.GenericsDefaultConstraints()} => {slotName}{memberGenerics}{interfaceMember.MethodArgs()};");
			}
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
