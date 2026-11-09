global using System;
global using System.Collections.Generic;
global using System.Diagnostics;
global using System.Linq;
global using System.Text;
global using System.Text.Json;
global using static Rinten.Internal.GlobalSystemNamespace;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo( "Rinten.Test.Unit" )]
[assembly: InternalsVisibleTo( "Rinten.Test.Engine" )]
[assembly: InternalsVisibleTo( "Rinten.Test.Integration" )]
[assembly: InternalsVisibleTo( "Benchmark" )]

[assembly: InternalsVisibleTo( "Rinten.Engine" )]
[assembly: InternalsVisibleTo( "Rinten.UI" )]
[assembly: InternalsVisibleTo( "Rinten.UI.Native" )]
[assembly: InternalsVisibleTo( "Rinten.Tools" )]
[assembly: InternalsVisibleTo( "Rinten.Bind" )]
[assembly: InternalsVisibleTo( "Rinten.Reflection" )]
[assembly: InternalsVisibleTo( "Rinten.Menu" )]
[assembly: InternalsVisibleTo( "Rinten.GameInstance" )]

//An agent's execute_code snippet - compiled in the editor to answer one question about the live
//engine, which is usually a question about something internal. See Editor.Mcp.ExecuteCode.
[assembly: InternalsVisibleTo( "McpSnippet" )]

//The Android launcher, which reads the engine's last log lines into a crash report -
//see RintenAndroid/CrashReport.cs. A phone has no console to read them off instead.
[assembly: InternalsVisibleTo( "Rinten-Android" )]

//And the web launcher, which says the engine's warnings in the browser's console - the one place a player or a developer can read them off a page.
[assembly: InternalsVisibleTo( "Rinten-Web" )]
[assembly: InternalsVisibleTo( "Facepunch.Interopgen" )]
[assembly: InternalsVisibleTo( "Rinten.AppSystem" )]
[assembly: InternalsVisibleTo( "Rinten" )]
[assembly: InternalsVisibleTo( "Sbox-Server" )]

[assembly: TasksPersistOnContextReset]
