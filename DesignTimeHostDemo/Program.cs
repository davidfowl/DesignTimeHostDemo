using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Framework.DesignTimeHost.Models;
using Microsoft.Framework.DesignTimeHost.Models.IncomingMessages;
using Microsoft.Framework.DesignTimeHost.Models.OutgoingMessages;
using Newtonsoft.Json.Linq;

namespace DesignTimeHostDemo
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: DesignTimeHostDemo [solutionPath]");
                return;
            }

            string runtimePath = GetRuntimePath();
            string applicationRoot = args[0];

            Go(runtimePath, applicationRoot);
        }

        private static string GetRuntimePath()
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetEnvironmentVariable("USERPROFILE");

            var kreHome = Path.Combine(home, ".kre");

            var aliasDirectory = Path.Combine(kreHome, "alias");

            var aliasFiles = new[] { "default.alias", "default.txt" };

            foreach (var shortAliasFile in aliasFiles)
            {
                var aliasFile = Path.Combine(aliasDirectory, shortAliasFile);

                if (File.Exists(aliasFile))
                {
                    var version = File.ReadAllText(aliasFile).Trim();

                    return Path.Combine(kreHome, "packages", version);
                }
            }

            throw new InvalidOperationException("Unable to locate default alias");
        }

        private static void Go(string runtimePath, string applicationRoot)
        {
            var hostId = Guid.NewGuid().ToString();
            var port = 1334;

            // Show runtime output
            var showRuntimeOutput = true;

            var workspace = new CustomWorkspace();

            StartRuntime(runtimePath, hostId, port, showRuntimeOutput, () =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                socket.Connect(new IPEndPoint(IPAddress.Loopback, port));

                var networkStream = new NetworkStream(socket);

                Console.WriteLine("Connected");

                var mapping = new Dictionary<int, string>();
                var projects = new Dictionary<string, int>();
                var workspaceProjects = new Dictionary<int, ConcurrentDictionary<string, ProjectId>>();
                var projectReferenceTodos = new ConcurrentDictionary<ProjectId, List<ProjectId>>();
                var queue = new ProcessingQueue(networkStream);

                var solution = workspace.AddSolution(SolutionInfo.Create(
                    SolutionId.CreateNewId(),
                    VersionStamp.Create()));

                queue.OnReceive += m =>
                {
                    // Get the project associated with this message
                    var projectPath = mapping[m.ContextId];

                    if (m.MessageType == "ProjectInformation")
                    {
                        var val = m.Payload.ToObject<ProjectMessage>();

                        foreach (var framework in val.Frameworks)
                        {
                            var id = workspaceProjects[m.ContextId].GetOrAdd(framework.FrameworkName, _ => ProjectId.CreateNewId());
                            var projectInfo = ProjectInfo.Create(
                                    id,
                                    VersionStamp.Create(),
                                    val.Name + "+" + framework.ShortName,
                                    val.Name,
                                    LanguageNames.CSharp);

                            solution = solution.AddProject(projectInfo);

                            List<ProjectId> references;
                            if (projectReferenceTodos.TryGetValue(id, out references))
                            {
                                lock (references)
                                {
                                    var reference = new Microsoft.CodeAnalysis.ProjectReference(id);

                                    foreach (var referenceId in references)
                                    {
                                        var project = solution.GetProject(referenceId);
                                        project = project.AddProjectReference(reference);
                                        solution = project.Solution;
                                    }

                                    references.Clear();
                                }
                            }
                        }
                    }
                    // This is where we can handle messages and update the
                    // language service
                    else if (m.MessageType == "References")
                    {
                        // References as well as the dependency graph information
                        var val = m.Payload.ToObject<ReferencesMessage>();

                        var projectId = workspaceProjects[m.ContextId][val.Framework.FrameworkName];

                        var project = solution.GetProject(projectId);

                        // TODO: Cache these
                        foreach (var file in val.FileReferences)
                        {
                            project = project.AddMetadataReference(MetadataReference.CreateFromFile(file));
                        }

                        foreach (var rawReference in val.RawReferences)
                        {
                            project = project.AddMetadataReference(MetadataReference.CreateFromImage(rawReference.Value));
                        }

                        foreach (var projectReference in val.ProjectReferences)
                        {
                            var projectContextId = projects[Path.GetDirectoryName(projectReference.Path)];
                            bool exists = true;
                            var projectReferenceId = workspaceProjects[projectContextId].GetOrAdd(projectReference.Framework.FrameworkName,
                                _ =>
                                {
                                    exists = false;
                                    return ProjectId.CreateNewId();
                                });

                            if (exists)
                            {
                                project = project.AddProjectReference(new Microsoft.CodeAnalysis.ProjectReference(projectReferenceId));
                            }
                            else
                            {
                                var references = projectReferenceTodos.GetOrAdd(projectReferenceId, _ => new List<ProjectId>());
                                lock (references)
                                {
                                    references.Add(projectId);
                                }
                            }

                        }

                        solution = project.Solution;
                    }
                    else if (m.MessageType == "CompilerOptions")
                    {
                        // Configuration and compiler options
                        var val = m.Payload.ToObject<CompilationOptionsMessage>();

                        var projectId = workspaceProjects[m.ContextId][val.Framework.FrameworkName];

                        var project = solution.GetProject(projectId);

                        project = project.WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                        solution = project.Solution;
                    }
                    else if (m.MessageType == "Sources")
                    {
                        // The sources to feed to the language service
                        var val = m.Payload.ToObject<SourcesMessage>();

                        var projectId = workspaceProjects[m.ContextId][val.Framework.FrameworkName];

                        var project = solution.GetProject(projectId);

                        foreach (var file in val.Files)
                        {
                            using (var stream = File.OpenRead(file))
                            {
                                var sourceText = SourceText.From(stream, encoding: Encoding.UTF8);
                                var document = project.AddDocument(file, sourceText);
                            }
                        }

                        solution = project.Solution;
                    }
                };

                // Start the message channel
                queue.Start();

                var solutionPath = applicationRoot;
                var watcher = new FileWatcher(solutionPath);
                int contextId = 0;

                foreach (var projectFile in Directory.EnumerateFiles(solutionPath, "project.json", SearchOption.AllDirectories))
                {
                    string projectPath = Path.GetDirectoryName(projectFile).TrimEnd(Path.DirectorySeparatorChar);

                    // Send an InitializeMessage for each project
                    var initializeMessage = new InitializeMessage
                    {
                        ProjectFolder = projectPath,
                    };

                    // Create a unique id for this project
                    int projectContextId = contextId++;

                    // Create a mapping from path to contextid and back
                    projects[projectPath] = projectContextId;
                    mapping[projectContextId] = projectPath;
                    workspaceProjects[projectContextId] = new ConcurrentDictionary<string, ProjectId>();

                    // Initialize this project
                    queue.Post(new Message
                    {
                        ContextId = projectContextId,
                        MessageType = "Initialize",
                        Payload = JToken.FromObject(initializeMessage),
                        HostId = hostId
                    });

                    // Watch the project.json file
                    watcher.WatchFile(Path.Combine(projectPath, "project.json"));
                    watcher.WatchDirectory(projectPath, ".cs");

                    // Watch all directories for cs files
                    foreach (var cs in Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories))
                    {
                        watcher.WatchFile(cs);
                    }

                    foreach (var d in Directory.GetDirectories(projectPath, "*.*", SearchOption.AllDirectories))
                    {
                        watcher.WatchDirectory(d, ".cs");
                    }
                }

                // When there's a file change
                watcher.OnChanged += changedPath =>
                {
                    foreach (var project in projects)
                    {
                        // If the project changed
                        if (changedPath.StartsWith(project.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            queue.Post(new Message
                            {
                                ContextId = project.Value,
                                MessageType = "FilesChanged",
                                HostId = hostId
                            });
                        }
                    }
                };
            });

            Console.WriteLine("Process Q to exit");

            while (true)
            {
                var ki = Console.ReadKey(true);

                if (ki.Key == ConsoleKey.Q)
                {
                    break;
                }
            }
        }

        private static void StartRuntime(string runtimePath,
                                         string hostId,
                                         int port,
                                         bool verboseOutput,
                                         Action OnStart)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(runtimePath, "bin", "klr.exe"),
                // CreateNoWindow = true,
                // UseShellExecute = false,
                Arguments = String.Format(@"--appbase ""{0}"" {1} {2} {3} {4}",
                                          Directory.GetCurrentDirectory(),
                                          Path.Combine(runtimePath, "bin", "lib", "Microsoft.Framework.DesignTimeHost", "Microsoft.Framework.DesignTimeHost.dll"),
                                          port,
                                          Process.GetCurrentProcess().Id,
                                          hostId),
            };

            Console.WriteLine(psi.FileName + " " + psi.Arguments);

            var kreProcess = Process.Start(psi);

            // Wait a little bit for it to conncet before firing the callback
            Thread.Sleep(1000);

            if (kreProcess.HasExited)
            {
                Console.WriteLine("Child process failed with {0}", kreProcess.ExitCode);
                return;
            }

            kreProcess.EnableRaisingEvents = true;
            kreProcess.Exited += (sender, e) =>
            {
                Console.WriteLine("Process crash trying again");

                Thread.Sleep(1000);

                StartRuntime(runtimePath, hostId, port, verboseOutput, OnStart);
            };

            OnStart();
        }

        private static Task ConnectAsync(Socket socket, IPEndPoint endPoint)
        {
            return Task.Factory.FromAsync((cb, state) => socket.BeginConnect(endPoint, cb, state), ar => socket.EndConnect(ar), null);
        }
    }
}
