using System.Reflection;
using System.Text;
using Sts2Ai.Brain;

// The commit this program was built from, as the build stamped it.
string commit = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+').ElementAtOrDefault(1)
    ?? "unknown";
using var requests = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), false, 1 << 16);
TokenServer.Serve(requests, Console.OpenStandardOutput(), commit);
