using Microsoft.CodeAnalysis;
using System;
using System.Linq;
using System.Text;
using System.Threading;

namespace MockGenereator
{
	[Generator]
	public class GenerateInterfaceGenerator : IIncrementalGenerator
	{
		public void Initialize(IncrementalGeneratorInitializationContext context)
		{
			context.RegisterPostInitializationOutput(static ctx =>
			{
				ctx.AddSource("GenerateInterfaceAttribute.cs", """
					using System;

					namespace MockGenerator
					{
						[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
						internal sealed class GenerateInterfaceAttribute : Attribute
						{
							public GenerateInterfaceAttribute() { }
							public GenerateInterfaceAttribute(string name) { Name = name; }
							public string Name { get; }
						}
					}
					""");
			});

			var src = context.SyntaxProvider.ForAttributeWithMetadataName(
				"MockGenerator.GenerateInterfaceAttribute",
				static (node, _) => true,
				Transform).WithTrackingName("MockGenereator.GenerateInterfaceGenerator");

			context.RegisterSourceOutput(src, static (spc, source) =>
			{
				if (source == null) return;
				spc.AddSource(source.HintName, source.Code);
			});
		}

		internal sealed class Result : IEquatable<Result>
		{
			public string HintName { get; init; }
			public string Code { get; init; }

			public bool Equals(Result other) => other != null && HintName == other.HintName && Code == other.Code;
			public override bool Equals(object obj) => obj is Result s && Equals(s);
			public override int GetHashCode() => (HintName?.GetHashCode() ?? 0) ^ (Code?.GetHashCode() ?? 0);
		}

		static Result Transform(GeneratorAttributeSyntaxContext context, CancellationToken token)
		{
			var typeSymbol = (INamedTypeSymbol)context.TargetSymbol;
			var ns = typeSymbol.ContainingNamespace.IsGlobalNamespace ? null : typeSymbol.ContainingNamespace.ToString();
			var className = typeSymbol.Name;

			string interfaceName = null;
			foreach (var attr in context.Attributes)
			{
				if (attr.AttributeClass?.Name != "GenerateInterfaceAttribute") continue;
				if (attr.ConstructorArguments.Length >= 1 && attr.ConstructorArguments[0].Value is string s && !string.IsNullOrWhiteSpace(s))
				{
					interfaceName = s;
				}
				break;
			}
			if (interfaceName == null)
			{
				interfaceName = "I" + className;
			}

			using var _ = StringBuilderHolder.Get(out var sb);

			var indent = ns != null ? "\t" : "";
			var memberIndent = indent + "\t";
			var generics = typeSymbol.TypeParameters.GenericsParams();
			var constraints = typeSymbol.TypeParameters.GenericsConstraints();

			var cref = typeSymbol.ToCref();
			if (ns != null)
			{
				sb.Append($$"""
					namespace {{ns}}
					{
						/// <summary>
						/// <see cref="{{cref}}"/>
						/// </summary>
						public interface {{interfaceName}}{{generics}}{{constraints}}
						{
					""");
			}
			else
			{
				sb.Append($$"""
					/// <summary>
					/// <see cref="{{cref}}"/>
					/// </summary>
					public interface {{interfaceName}}{{generics}}{{constraints}}
					{
					""");
			}

			foreach (var member in typeSymbol.GetMembers())
			{
				if (member.DeclaredAccessibility != Accessibility.Public) continue;
				if (member.IsStatic) continue;

				switch (member)
				{
					case IPropertySymbol p when !p.IsIndexer:
					{
						var getterPublic = p.GetMethod != null && p.GetMethod.DeclaredAccessibility == Accessibility.Public;
						var setterPublic = p.SetMethod != null && p.SetMethod.DeclaredAccessibility == Accessibility.Public;
						if (!getterPublic && !setterPublic) break;
						var accessors = (getterPublic ? "get; " : "") + (setterPublic ? "set; " : "");
						sb.Append($"\n{memberIndent}{p.Type.QualifiedName()} {p.Name} {{ {accessors}}}");
						break;
					}
					case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary:
					{
						if (m.OverriddenMethod?.ContainingType?.SpecialType == SpecialType.System_Object) break;
						var mGenerics = m.TypeParameters.GenericsParams();
						var mConstraints = m.TypeParameters.GenericsConstraints();
						sb.Append($"\n{memberIndent}{m.ReturnTypeName()} {m.Name}{mGenerics}{m.MethodParams(withDefaults: true)}{mConstraints};");
						break;
					}
					case IEventSymbol e:
					{
						sb.Append($"\n{memberIndent}event {e.Type.QualifiedName()} {e.Name};");
						break;
					}
				}
			}

			sb.Append($"\n{indent}}}");

			var qualifiedInterface = ns != null ? $"{ns}.{interfaceName}" : interfaceName;
			if (ns != null)
			{
				sb.Append($$"""


						partial class {{className}}{{generics}} : {{qualifiedInterface}}{{generics}}{{constraints}}
						{
						}
					}
					""");
			}
			else
			{
				sb.Append($$"""


					partial class {{className}}{{generics}} : {{qualifiedInterface}}{{generics}}{{constraints}}
					{
					}
					""");
			}

			var hint = (ns == null ? "" : ns + ".") + className + ".GenerateInterface.cs";
			return new Result { HintName = hint, Code = sb.ToString() };
		}
	}
}
