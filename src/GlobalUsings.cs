// Project-wide implicit usings. NOTE: the Microsoft.NET.Sdk (WPF) ImplicitUsings set does
// NOT include System.IO, System.Linq is included but System.IO is not - which is why files
// using File/Directory/Path otherwise need a manual "using System.IO;". Declaring it here
// once fixes that everywhere.
global using System.IO;
global using System.IO.Compression;
global using System.Collections.ObjectModel;
global using System.Text.Json;
global using System.Text.RegularExpressions;
global using DDS2ModManager.Models;
global using DDS2ModManager.Services;
