// Mirrors src/GlobalUsings.cs. Stated explicitly rather than relying on ImplicitUsings:
// a WPF-targeting test project does not reliably get System.IO from it, and the failure mode
// is a wall of "the name 'Path' does not exist" that says nothing about the cause.
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
