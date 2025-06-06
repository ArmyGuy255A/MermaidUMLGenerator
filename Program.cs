using System.Text;
using MermaidUmlGeneratorTool.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MermaidUmlGeneratorTool;

static class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            await Console.Error.WriteLineAsync(
                "Usage: MermaidUmlGeneratorTool <path-to-csproj-or-sln> [--outputDir <OutputDirectory>] [--disableClasses] [--disableInterfaces] [--disableEnums] [--enableNestedInheritance] [--enableNamespaces]");
            return;
        }

        var projectPath = args[0];
        string? outputDir = null;

        bool disableClasses = false;
        bool disableInterfaces = false;
        bool disableEnums = false;
        bool enableNestedInheritance = false;
        bool enableNamespaces = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--outputDir":
                    if (i + 1 < args.Length) outputDir = args[++i];
                    break;
                case "--disableClasses": disableClasses = true; break;
                case "--disableInterfaces": disableInterfaces = true; break;
                case "--disableEnums": disableEnums = true; break;
                case "--enableNestedInheritance": enableNestedInheritance = true; break;
                case "--enableNamespaces": enableNamespaces = true; break;
            }
        }

        outputDir ??= Directory.GetCurrentDirectory();

        var workspace = MSBuildLocatorUtilities.CreateWorkspace();
        var project = await MSBuildLocatorUtilities.GetProjectFromPath(workspace, projectPath);

        if (null == project)
        {
            await Console.Error.WriteLineAsync("Could not find a valid project.");
            return;
        }

        var compilation = await project.GetCompilationAsync();
        if (compilation == null)
        {
            await Console.Error.WriteLineAsync("Compilation failed.");
            return;
        }

        List<MermaidData> mermaidDataList = [];

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = await syntaxTree.GetRootAsync();

            var typeDeclarations = root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Where(t => t is ClassDeclarationSyntax or InterfaceDeclarationSyntax);

            foreach (var typeDecl in typeDeclarations)
            {
                var symbol = semanticModel.GetDeclaredSymbol(typeDecl);
                if (symbol == null) continue;

                var classType = symbol.TypeKind switch
                {
                    TypeKind.Interface => MermaidClassType.Interface,
                    _ => MermaidClassType.Class
                };

                var data = new MermaidData
                {
                    Name = symbol.Name,
                    IsAbstract = symbol.IsAbstract,
                    ClassType = classType,
                    Visibility = SymbolExtensions.GetMermaidVisibility(symbol.DeclaredAccessibility),
                    Properties = symbol.GetMembers().OfType<IPropertySymbol>()
                        .Where(p => !p.IsImplicitlyDeclared)
                        .Select(p => new MermaidProperty
                        {
                            Name = p.Name,
                            Type = SymbolExtensions.GetTypeDisplayName(p.Type),
                            Visibility = SymbolExtensions.GetMermaidVisibility(p.DeclaredAccessibility),
                            IsCollection = SymbolExtensions.IsCollection(p.Type) || p.Type is IArrayTypeSymbol
                        }).ToList(),
                    Methods = symbol.GetMembers().OfType<IMethodSymbol>()
                        .Where(m => m is { IsImplicitlyDeclared: false, MethodKind: MethodKind.Ordinary })
                        .Select(m => new MermaidMethod
                        {
                            Name = m.Name,
                            ReturnType = m.ReturnType.Name,
                            Visibility = SymbolExtensions.GetMermaidVisibility(m.DeclaredAccessibility),
                            Parameters = m.Parameters.Select(p => $"{p.Type.Name} {p.Name}").ToList(),
                            IsAsync = m.IsAsync
                        }).ToList(),
                    Relationships = new List<MermaidRelationship>(),
                    Namespace = symbol.ContainingNamespace.ToString()
                };

                if (enableNestedInheritance)
                {
                    foreach (var baseType in symbol.BaseTypes())
                    {
                        if (baseType.Name != "Object")
                        {
                            data.Relationships.Add(new MermaidRelationship
                            {
                                From = symbol.Name,
                                To = baseType.Name,
                                LinkType = MermaidLinkType.Solid,
                                Relationship = MermaidRelationshipType.Inheritance
                            });
                        }
                    }
                }
                else
                {
                    // Add direct inheritance only
                    var directBase = symbol.BaseType;
                    if (directBase != null && directBase.Name != "Object")
                    {
                        data.Relationships.Add(new MermaidRelationship
                        {
                            From = symbol.Name,
                            To = directBase.Name,
                            LinkType = MermaidLinkType.Solid,
                            Relationship = MermaidRelationshipType.Inheritance
                        });
                    }
                }

                // Add interface implementations
                foreach (var iface in symbol.Interfaces)
                {
                    data.Relationships.Add(new MermaidRelationship
                    {
                        From = symbol.Name,
                        To = iface.Name,
                        LinkType = MermaidLinkType.Dashed,
                        Relationship = classType == MermaidClassType.Interface
                            ? MermaidRelationshipType.Inheritance
                            : MermaidRelationshipType.Realization
                    });
                }

                // Add property-type-based relationships (association, aggregation, dependency)
                foreach (var prop in symbol.GetMembers().OfType<IPropertySymbol>())
                {
                    ITypeSymbol propType = prop.Type;

                    string targetTypeName = propType.Name;

                    // Handle arrays
                    if (propType is IArrayTypeSymbol arrayType)
                    {
                        targetTypeName = arrayType.ElementType.Name;
                        propType = arrayType.ElementType;
                    }

                    // Unwrap generic types (e.g., List<IFeature>)
                    if (propType is INamedTypeSymbol { TypeArguments.Length: 1 } named)
                    {
                        propType = named.TypeArguments[0];
                        targetTypeName = propType.Name;
                    }

                    // Skip common system types
                    if (propType.ContainingNamespace?.ToString()?.StartsWith("System") == true)
                        continue;

                    MermaidRelationshipType relationshipType = propType.TypeKind switch
                    {
                        TypeKind.Enum => MermaidRelationshipType.Dependency,
                        _ => SymbolExtensions.IsCollection(prop.Type)
                            ? MermaidRelationshipType.Aggregation
                            : MermaidRelationshipType.Association
                    };

                    var (from, to) = relationshipType switch
                    {
                        MermaidRelationshipType.Aggregation => (targetTypeName, symbol.Name),
                        _ => (symbol.Name, targetTypeName)
                    };
                    var relationship = new MermaidRelationship
                    {
                        From = from,
                        To = to,
                        LinkType = MermaidLinkType.Solid,
                        Relationship = relationshipType
                    };

                    if (!data.Relationships.Any(r =>
                            r.From == relationship.From && r.To == relationship.To &&
                            r.Relationship == relationship.Relationship))
                    {
                        data.Relationships.Add(relationship);
                    }
                }

                mermaidDataList.Add(data);
            }

            var enumDeclarations = root.DescendantNodes().OfType<EnumDeclarationSyntax>();
            foreach (var en in enumDeclarations)
            {
                mermaidDataList.Add(new MermaidData
                {
                    Name = en.Identifier.Text,
                    IsAbstract = false,
                    ClassType = MermaidClassType.Enum,
                    Visibility = MermaidVisibilityType.Public,
                    Properties = en.Members.Select(m => new MermaidProperty
                    {
                        Name = m.Identifier.Text,
                        Type = "enum",
                        Visibility = MermaidVisibilityType.Public
                    }).ToList()
                });
            }
        }

        // Filter out classes, interfaces, and enums based on command line options
        mermaidDataList = mermaidDataList
            .Where(d =>
                !(disableClasses && d.ClassType == MermaidClassType.Class) &&
                !(disableInterfaces && d.ClassType == MermaidClassType.Interface) &&
                !(disableEnums && d.ClassType == MermaidClassType.Enum)
            ).ToList();

        var projectName = Path.GetFileNameWithoutExtension(project.FilePath);
        // Build a good filename based on the types included in the project
        var outputFileName = $"{projectName}";
        if (disableClasses) outputFileName += "_NoClasses";
        if (disableInterfaces) outputFileName += "_NoInterfaces";
        if (disableEnums) outputFileName += "_NoEnums";
        if (enableNestedInheritance) outputFileName += "_NestedInheritance";
        if (enableNamespaces) outputFileName += "_WithNamespaces";
        outputFileName += ".md";
        var outputPath = Path.Combine(outputDir, outputFileName);

        Console.WriteLine($"Generating Mermaid UML for project: {projectName}");
        // var mermaidOutput = SymbolExtensions.GenerateMermaidFileData(mermaidDataList, enableNamespaces);
        var mermaidOutput = SymbolExtensions.GenerateMermaidFileData(
            mermaidDataList,
            enableNamespaces,
            enableNestedInheritance
        );
        await File.WriteAllTextAsync(outputPath, mermaidOutput);
        Console.WriteLine($"UML diagram saved to: {outputPath}");
    }
}

public enum MermaidVisibilityType
{
    Public,
    Private,
    Protected,
    Internal,
    ProtectedOrInternal,
    Unknown
}

public enum MermaidClassType
{
    Interface,
    Class,
    Enum
}

public enum MermaidRelationshipType
{
    Inheritance,
    Composition,
    Aggregation,
    Association,
    Dependency,
    Realization,
    Link
}

public enum MermaidLinkType
{
    Solid,
    Dashed
}

public class MermaidProperty
{
    public MermaidVisibilityType Visibility { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsCollection { get; init; } // e.g., "List<string>", "IEnumerable<int>"

    public string ToMermaidString()
    {
        var vis = SymbolExtensions.GetMermaidVisibilityString(Visibility);
        var result = $"{vis} {Type} {Name}";
        return result;
    }
}

public class MermaidMethod
{
    public MermaidVisibilityType Visibility { get; init; }
    public string ReturnType { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public List<string> Parameters { get; init; } = new List<string>(); // e.g., "int id", "string name"
    public bool IsAsync { get; init; }

    public string ToMermaidString()
    {
        var asyncPrefix = IsAsync ? "async " : "";
        var paramsString = Parameters.Count > 0 ? $"({string.Join(", ", Parameters)})" : "()";
        var vis = SymbolExtensions.GetMermaidVisibilityString(Visibility);

        var result = $"{vis} {asyncPrefix}{ReturnType} {Name}{paramsString}";
        return result;
    }
}

public class MermaidRelationship
{
    public MermaidRelationshipType Relationship { get; init; }
    public MermaidLinkType LinkType { get; init; }
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;

    public string ToMermaidString()
    {
        var rel = SymbolExtensions.GetMermaidRelationshipString(Relationship);

        var context = SymbolExtensions.GetMermaidRelationshipContextString(Relationship);

        var link = SymbolExtensions.GetMermaidLinkTypeString(LinkType, Relationship);

        var result = $"    {From} {link}{rel} {To} : {context}";
        return result;
    }
}

public class MermaidData
{
    public MermaidClassType ClassType { get; init; }
    public bool IsAbstract { get; init; }
    public string Name { get; init; } = string.Empty;

    public string? Namespace { get; init; } = string.Empty; // Optional namespace for the class
    public MermaidVisibilityType Visibility { get; set; }
    public List<MermaidProperty> Properties { get; init; } = [];
    public List<MermaidMethod> Methods { get; init; } = [];
    public List<MermaidRelationship> Relationships { get; init; } = [];


    public string ToMermaidString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"    class {Name} {{");

        foreach (var prop in Properties)
        {
            sb.AppendLine($"        {prop.ToMermaidString()}");
        }

        foreach (var method in Methods)
        {
            sb.AppendLine($"        {method.ToMermaidString()}");
        }

        sb.AppendLine("    }");
        if (IsAbstract && ClassType == MermaidClassType.Class)
            sb.AppendLine($"    <<abstract>> {Name}");
        else
            sb.AppendLine($"    <<{ClassType.ToString()}>> {Name}");
        
        foreach (var rel in Relationships)
        {
            sb.AppendLine(rel.ToMermaidString());
        }

        return sb.ToString();
    }
}

public static class SymbolExtensions
{
    public static IEnumerable<INamedTypeSymbol> BaseTypes(this INamedTypeSymbol symbol)
    {
        var current = symbol.BaseType;
        while (current != null)
        {
            yield return current;
            current = current.BaseType;
        }
    }

    public static MermaidVisibilityType GetMermaidVisibility(Accessibility accessibility)
    {
        return accessibility switch
        {
            Accessibility.Public => MermaidVisibilityType.Public,
            Accessibility.Private => MermaidVisibilityType.Private,
            Accessibility.Protected => MermaidVisibilityType.Protected,
            Accessibility.Internal => MermaidVisibilityType.Internal,
            Accessibility.ProtectedOrInternal => MermaidVisibilityType.ProtectedOrInternal,
            _ => MermaidVisibilityType.Unknown
        };
    }

    public static string GetMermaidVisibilityString(MermaidVisibilityType symbol)
    {
        return symbol switch
        {
            MermaidVisibilityType.Public => "+",
            MermaidVisibilityType.Private => "-",
            MermaidVisibilityType.Protected => "#",
            MermaidVisibilityType.Internal => "~",
            MermaidVisibilityType.ProtectedOrInternal => "~",
            _ => "?"
        };
    }

    public static string GetMermaidRelationshipString(MermaidRelationshipType relationshipType)
    {
        return relationshipType switch
        {
            MermaidRelationshipType.Inheritance => "|>",
            MermaidRelationshipType.Composition => "*",
            MermaidRelationshipType.Aggregation => "o",
            MermaidRelationshipType.Association => ">",
            MermaidRelationshipType.Realization => "|>",
            MermaidRelationshipType.Dependency => ">",
            MermaidRelationshipType.Link => "",
            _ => "?"
        };
    }

    public static string GetMermaidRelationshipContextString(MermaidRelationshipType relationshipType)
    {
        return relationshipType switch
        {
            MermaidRelationshipType.Inheritance => "inherits",
            MermaidRelationshipType.Composition => "composes",
            MermaidRelationshipType.Aggregation => "aggregates",
            MermaidRelationshipType.Association => "associates",
            MermaidRelationshipType.Realization => "realizes",
            MermaidRelationshipType.Dependency => "depends on",
            MermaidRelationshipType.Link => "links",
            _ => "unknown"
        };
    }

    public static string GetMermaidLinkTypeString(MermaidLinkType linkType, MermaidRelationshipType relationshipType)
    {
        return relationshipType switch
        {
            MermaidRelationshipType.Realization => "..",
            MermaidRelationshipType.Dependency => "..",
            MermaidRelationshipType.Link => "..",
            _ => "--"
        };
    }


    public static bool IsCollection(ITypeSymbol type)
    {
        if (type.Name.ToLower() is "string")
        {
            return false;
        }

        return type.Name is "IEnumerable" or "ICollection" or "List" ||
               type.AllInterfaces.Any(i => i.Name is "IEnumerable" or "ICollection");
    }

    public static string GetTypeDisplayName(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return $"{array.ElementType.Name}[]";

            case INamedTypeSymbol { IsGenericType: true } named:
                var typeArgs = string.Join(", ", named.TypeArguments.Select(t => t.Name));
                return $"{named.Name}<{typeArgs}>";

            default:
                return type.Name;
        }
    }


    public static string GenerateMermaidFileData(
        List<MermaidData> mermaidData,
        bool groupByNamespace,
        bool enableNestedInheritance
    )

    {
        var sb = new StringBuilder();
        sb.AppendLine("```mermaid");

        sb.AppendLine("---");
        sb.AppendLine($"title: {mermaidData.FirstOrDefault()?.Name ?? "UML Diagram"}");
        sb.AppendLine("config:");
        sb.AppendLine("  class:");
        sb.AppendLine("    hideEmptyMembersBox: true");
        sb.AppendLine("---");

        sb.AppendLine("classDiagram");

        if (groupByNamespace)
        {
            // Group with namespaces
            var grouped = mermaidData
                .GroupBy(d => string.IsNullOrWhiteSpace(d.Namespace) ? null : d.Namespace)
                .OrderBy(g => g.Key);

            // First: define all classes grouped by namespace
            foreach (var group in grouped)
            {
                if (!string.IsNullOrWhiteSpace(group.Key))
                {
                    sb.AppendLine($"    namespace {group.Key.Replace(".", "-")} {{");
                    foreach (var data in group)
                    {
                        sb.AppendLine($"        class {data.Name} {{");

                        foreach (var prop in data.Properties)
                            sb.AppendLine($"            {prop.ToMermaidString()}");

                        foreach (var method in data.Methods)
                            sb.AppendLine($"            {method.ToMermaidString()}");

                        sb.AppendLine("        }");
                    }

                    sb.AppendLine("    }");
                }
                else
                {
                    foreach (var data in group)
                    {
                        sb.AppendLine($"    class {data.Name} {{");

                        foreach (var prop in data.Properties)
                            sb.AppendLine($"        {prop.ToMermaidString()}");

                        foreach (var method in data.Methods)
                            sb.AppendLine($"        {method.ToMermaidString()}");

                        sb.AppendLine("    }");
                    }
                }
            }

            sb.AppendLine();

            // Second: add class stereotypes (<<Class>>, <<Interface>>, etc.)
            foreach (var data in mermaidData)
            {
                sb.AppendLine($"    <<{data.ClassType}>> {data.Name}");
            }

            sb.AppendLine();

            // Third: add relationships
            foreach (var data in mermaidData)
            {
                foreach (var rel in data.Relationships)
                {
                    sb.AppendLine(rel.ToMermaidString());
                }
            }
        }
        else
        {
            // Group without namespaces
            foreach (var data in mermaidData)
            {
                sb.Append(data.ToMermaidString());
            }
        }

        sb.AppendLine("```");
        return sb.ToString();
    }
}