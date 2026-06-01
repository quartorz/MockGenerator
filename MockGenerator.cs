using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MockGenerator;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;

namespace MockGenereator
{
	[Generator]
	public class MockGenerator : IIncrementalGenerator
	{
		public void Initialize(IncrementalGeneratorInitializationContext context)
		{
			context.RegisterPostInitializationOutput(ctx =>
			{
				ctx.AddSource("GenerateViewInterfacesAttribute.cs", """
					using System;

					namespace MockGenerator
					{
						[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
						internal sealed class GenerateViewInterfacesAttribute : Attribute
						{
						}
					}
					""");
				ctx.AddSource("InputAttribute.cs", """
					using System;

					namespace MockGenerator
					{
						[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Event, AllowMultiple = false, Inherited = false)]
						internal sealed class InputAttribute : Attribute
						{
						}
					}
					""");
				ctx.AddSource("OutputAttribute.cs", """
					using System;

					namespace MockGenerator
					{
						[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Event, AllowMultiple = false, Inherited = false)]
						internal sealed class OutputAttribute : Attribute
						{
						}
					}
					""");
				ctx.AddSource("GenerateMockViewAttribute.cs", """
					using System;

					namespace MockGenerator
					{
						[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
						internal sealed class GenerateMockViewAttribute : Attribute
						{
						}
					}
					""");
			});

			//var source = context.SyntaxProvider.CreateSyntaxProvider(
			//	static (node, token) => true,
			//	static (ctx, token) =>
			//	{
			//		ctx.Node.SyntaxTree.
			//		return 0;				
			//	});

			context.RegisterSourceOutput(
				context.SyntaxProvider.ForAttributeWithMetadataName(
					"MockGenerator.GenerateViewInterfacesAttribute",
					static (node, token) => true,
					static (ctx, token) => ctx),
				GenerateViewInterfaces);
			//context.RegisterSourceOutput(
			//	context.SyntaxProvider.ForAttributeWithMetadataName(
			//		"MockGenerator.GenerateViewInterfacesAttribute",
			//		static (node, token) => true,
			//		Transform),
			//	(_, _) => { });

			context.RegisterSourceOutput(
				context.SyntaxProvider.ForAttributeWithMetadataName(
					"MockGenerator.GenerateMockViewAttribute",
					static (node, token) => true,
					static (ctx, token) => ctx),
				GenerateMockView);
		}

		static Source Transform(GeneratorAttributeSyntaxContext source, CancellationToken token)
		{
			var typeSymbol = (INamedTypeSymbol)source.TargetSymbol;
			var typeNode = (TypeDeclarationSyntax)source.TargetNode;
			return new Source(
				typeSymbol.ContainingNamespace.IsGlobalNamespace ? null : typeSymbol.ContainingNamespace.ToString(), typeSymbol.Name, null, [new("", "b")], [], [], [] ,[]);
		}

		static void RunGenerator(SourceProductionContext context, GeneratorAttributeSyntaxContext source,
			string hintName, Action<SourceProductionContext, GeneratorAttributeSyntaxContext, StringBuilder> generator)
		{
			using var _ = StringBuilderHolder.Get(out var sb);
			try
			{
				generator(context, source, sb);
			}
			catch (Exception e)
			{
				Errors.Unexpected(context, e);
				sb.AppendFormat("\n#error {0}", e.Message);
			}
			context.AddSource(hintName, sb.ToString());
		}

		static void GenerateViewInterfaces(SourceProductionContext context, GeneratorAttributeSyntaxContext source)
		{
			var typeSymbol = (INamedTypeSymbol)source.TargetSymbol;
			var typeNode = (TypeDeclarationSyntax)source.TargetNode;

			var @in = new List<ISymbol>();
			var @out = new List<ISymbol>();
			var inout = new List<ISymbol>();
			foreach (var member in typeSymbol.GetMembers())
			{
				if (member.HasAttribute("MockGenerator", "InputAttribute"))
				{
					if (member.HasAttribute("MockGenerator", "OutputAttribute"))
					{
						inout.Add(member);
					}
					else
					{
						@in.Add(member);
					}
				}
				else if (member.HasAttribute("MockGenerator", "OutputAttribute"))
				{
					@out.Add(member);
				}
			}

			RunGenerator(context, source, $"{Namespace(typeSymbol)}.I{typeSymbol.Name}Input.cs", (context, syntax, sb) =>
			{
				sb.Append($$"""
				namespace {{Namespace(typeSymbol)}}
				{
					public interface I{{typeSymbol.Name}}Input{{typeSymbol.TypeParameters.GenericsParams()}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
					{
				""");

				foreach (var member in @in.Concat(inout))
				{
					if (member is IFieldSymbol field)
					{
						var ifield = field.Type.AllInterfaces.FirstOrDefault(static x => x.HasAttribute("MockGenerator", "InputAttribute"));
						if (ifield != null)
						{
							sb.Append($"\n		{ifield.QualifiedName()} {field.ToPropertyName()} {{ get; }}");
						}
						else
						{
							Errors.IsNotInput(context, field);
						}
					}
					else if (member is IPropertySymbol property)
					{
						var iprop = property.Type.AllInterfaces.FirstOrDefault(static x => x.HasAttribute("MockGenerator", "InputAttribute"));
						if (iprop != null)
						{
							sb.Append($"\n		{iprop.QualifiedName()} {property.Name} {{ get; }}");
						}
						else
						{
							sb.Append($"\n		{property.Type.QualifiedName()} {property.Name} {{ set; }}");
						}
					}
					else if (member is IEventSymbol @event)
					{
						sb.Append($"\n		event {@event.Type.QualifiedName()} {@event.Name};");
					}
				}

				sb.Append("""

					}
				}
				""");
			});

			RunGenerator(context, source, $"{Namespace(typeSymbol)}.I{typeSymbol.Name}Output.cs", (context, source, sb) =>
			{
				sb.Append($$"""
				namespace {{Namespace(typeSymbol)}}
				{
					public interface I{{typeSymbol.Name}}Output{{typeSymbol.TypeParameters.GenericsParams()}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
					{
				""");

				foreach (var member in @out.Concat(inout))
				{
					if (member is IFieldSymbol field)
					{
						var ifield = field.Type.AllInterfaces.FirstOrDefault(static x => x.HasAttribute("MockGenerator", "OutputAttribute"));
						if (ifield != null)
						{
							sb.Append($"\n		{ifield.QualifiedName()} {field.ToPropertyName()} {{ get; }}");
						}
						else
						{
							Errors.IsNotOutput(context, field);
						}
					}
					else if (member is IMethodSymbol method)
					{
						sb.Append($"\n		{method.ReturnTypeName()} {method.Name}(");
						foreach (var (param, index) in method.Parameters.Select(static (param, index) => (param, index)))
						{
							if (index != 0)
							{
								sb.Append(", ");
							}
							sb.Append($"{param.Type.QualifiedName()} {param.Name}");
						}
						sb.Append(");");
					}
					else if (member is IPropertySymbol property)
					{
						sb.Append($"\n		{property.Type.QualifiedName()} {property.Name} {{ set; }}");
					}
					else if (member is IEventSymbol @event)
					{
						sb.Append($"\n		event {@event.Type.QualifiedName()} {@event.Name};");
					}
				}

				sb.Append("""

					}
				}
				""");
			});

			RunGenerator(context, source, $"{(typeSymbol.ContainingNamespace.IsGlobalNamespace ? "global" : typeSymbol.ContainingNamespace)}.I{typeSymbol.Name}.cs", (context, source, sb) =>
			{
				sb.Append($$"""
				namespace {{Namespace(typeSymbol)}}
				{
					[MockGenerator.Input, MockGenerator.Output]
					public interface I{{typeSymbol.Name}}{{typeSymbol.TypeParameters.GenericsParams()}} :
						I{{typeSymbol.Name}}Input{{typeSymbol.TypeParameters.GenericsParams()}},
						I{{typeSymbol.Name}}Output{{typeSymbol.TypeParameters.GenericsParams()}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
					{
					}
				}
				""");
			});

			RunGenerator(context, source, $"{(typeSymbol.ContainingNamespace.IsGlobalNamespace ? "global" : typeSymbol.ContainingNamespace)}.{typeSymbol.Name}.cs", (context, source, sb) =>
			{
				if (!typeSymbol.ContainingNamespace.IsGlobalNamespace)
				{
					sb.Append($$"""
					namespace {{typeSymbol.ContainingNamespace}}
					{
					""");
				}

				sb.Append($$"""

					public partial class {{typeSymbol.Name}}{{typeSymbol.TypeParameters.GenericsParams()}} :
						{{Namespace(typeSymbol)}}.I{{typeSymbol.Name}}{{typeSymbol.TypeParameters.GenericsParams()}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
					{
				""");

				foreach (var member in @in)
				{
					if (member is IFieldSymbol field)
					{
						var ifield = field.Type.AllInterfaces.FirstOrDefault(static x => x.HasAttribute("MockGenerator", "InputAttribute"));
						if (ifield != null)
						{
							sb.Append($"\n		public {ifield.QualifiedName()} {field.ToPropertyName()} => {field.Name};");
						}
					}
				}

				foreach (var member in @out)
				{
					if (member is IFieldSymbol field)
					{
						var ifield = field.Type.AllInterfaces.FirstOrDefault(static x => x.HasAttribute("MockGenerator", "OutputAttribute"));
						if (ifield != null)
						{
							sb.Append($"\n		public {ifield.QualifiedName()} {field.ToPropertyName()} => {field.Name};");
						}
					}
				}

				foreach (var member in inout)
				{
					if (member is IFieldSymbol field)
					{
						var ifield = field.Type.AllInterfaces.FirstOrDefault(static x =>
							x.HasAttribute("MockGenerator", "InputAttribute") && x.HasAttribute("MockGenerator", "OutputAttribute"));
						if (ifield != null)
						{
							sb.Append($"\n		public {ifield.QualifiedName()} {field.ToPropertyName()} => {field.Name};");
						}
						else
						{
							Errors.IsNotInputAndOutput(context, field);
						}
					}
				}

				sb.Append("""

					}
				""");

				if (!typeSymbol.ContainingNamespace.IsGlobalNamespace)
				{
					sb.Append("""

					}
					""");
				}
			});
		}

		static void GenerateMockView(SourceProductionContext context, GeneratorAttributeSyntaxContext source)
		{
			var typeSymbol = (INamedTypeSymbol)source.TargetSymbol;
			var typeNode = (TypeDeclarationSyntax)source.TargetNode;

			RunGenerator(context, source, $"{Namespace(typeSymbol)}.Mock{typeSymbol.Name}.cs", (context, source, sb) =>
			{
				sb.Append($$"""
					namespace {{Namespace(typeSymbol)}}
					{
						/// <summary>
						/// <see cref="{{typeSymbol.ToCref()}}"/>
						/// </summary>
						public partial class Mock{{typeSymbol.Name}}{{typeSymbol.TypeParameters.GenericsParams()}} : I{{typeSymbol.Name}}{{typeSymbol.TypeParameters.GenericsParams()}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
						{
					""");

				foreach (var member in typeSymbol.GetMembers())
				{
					var isInput = member.HasAttribute("MockGenerator", "InputAttribute");
					var isOutput = member.HasAttribute("MockGenerator", "OutputAttribute");
					if (!isInput && !isOutput)
					{
						continue;
					}
					if (member is IFieldSymbol field)
					{
						INamedTypeSymbol type = null;
						if (isInput && isOutput)
						{
							type = field.Type.AllInterfaces.FirstOrDefault(static x => x.HasAttribute("MockGenerator", "InputAttribute") && x.HasAttribute("MockGenerator", "OutputAttribute"));
							if (type == null)
							{
								Errors.IsNotInputAndOutput(context, field);
							}
						}
						else if (isInput)
						{
							type = field.Type.AllInterfaces.FirstOrDefault(static x => x.HasAttribute("MockGenerator", "InputAttribute"));
							if (type == null)
							{
								Errors.IsNotInput(context, field);
							}
						}
						else if (isOutput)
						{
							type = field.Type.AllInterfaces.FirstOrDefault(static x => x.HasAttribute("MockGenerator", "OutputAttribute"));
							if (type == null)
							{
								Errors.IsNotOutput(context, field);
							}
						}
						sb.AppendFormat("\n		public {0} {1} {{ get; set; }}", type == null ? field.Type.QualifiedName() : type.QualifiedName(), field.ToPropertyName());
					}
					else if (member is IPropertySymbol property)
					{
						sb.AppendFormat("\n		public {0} {1} {{ get; set; }}", property.Type.QualifiedName(), property.Name);
					}
					else if (member is IMethodSymbol method)
					{
						sb.EmitMockMethodMember("\t\t", method);
					}
					else if (member is IEventSymbol @event)
					{
						sb.EmitMockEventMember("\t\t", @event);
					}
					else
					{
						throw new Exception($"unsupported member type {member.GetType()}");
					}
				}

				sb.Append("""

						}
					}
					""");
			});
		}

		static string Namespace(ISymbol symbol)
		{
			if (symbol.ContainingNamespace.IsGlobalNamespace)
			{
				return "MockView";
			}
			else
			{
				return $"MockView.{symbol.ContainingNamespace}";
			}
		}

	}
}
