# MermaidUMLGenerator

A command-line tool to generate [Mermaid](https://mermaid-js.github.io/) UML class diagrams from C# projects or solutions using Roslyn.

## Prerequisites

- [.NET 9.0 SDK or later](https://dotnet.microsoft.com/download)
- The project you want to analyze must be buildable (all dependencies restored).

## Building

Clone this repository and build the project:

```sh
git clone <your-repo-url>
cd MermaidUMLGenerator
dotnet build
```

## Usage
Run the tool from the command line, providing the path to the C# project or solution:

```sh   
dotnet run --project MermaidUMLGenerator <path-to-csproj-or-sln> [options]
```

### Options
- `--outputDir <path>`: Specify the output file path for the generated Mermaid diagram (default: `mermaid-diagram.mmd`).
- `--disableClasses`: Exclude classes from the diagram.
- `--disableInterfaces`: Exclude interfaces from the diagram.
- `--disableEnums`: Exclude enums from the diagram.
- `--enableNestedInheritance`: Show full inheritance chains (not just direct inheritance).
- `--enableNamespaces`: Group classes by namespace in the diagram.
  
### Example

```sh
dotnet run --project MermaidUMLGenerator MySolution.sln --enableNamespaces --outputDir diagrams
```

## Output
The generated Mermaid diagram will be saved in the specified output directory with the `.md` extension. You can visualize it using any Mermaid-compatible viewer or editor.

## License
This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.