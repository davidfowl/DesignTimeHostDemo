using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Recommendations;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Framework.DesignTimeHost.Models;
using Microsoft.Framework.DesignTimeHost.Models.IncomingMessages;
using Microsoft.Framework.DesignTimeHost.Models.OutgoingMessages;
using Newtonsoft.Json.Linq;

namespace DesignTimeHostDemo
{
    class Program
    {
        static Process _kreProcess;
        static bool ended = false;

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

                    Console.WriteLine("Using KRE version '{0}'.", version);

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

            Workspace workspace = null;
            var projects = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var projectState = new Dictionary<int, ProjectState>();
            var projectReferenceTodos = new ConcurrentDictionary<ProjectId, List<ProjectId>>();

            StartRuntime(runtimePath, hostId, port, showRuntimeOutput, () =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Connect(new IPEndPoint(IPAddress.Loopback, port));

                var networkStream = new NetworkStream(socket);

                Console.WriteLine("Connected");

                var solutionPath = applicationRoot;
                var watcher = new FileWatcher(solutionPath);
                var queue = new ProcessingQueue(networkStream);

                var cw = new MyCustomWorkspace();

                workspace = cw;

                queue.OnReceive += m =>
                {
                    var state = projectState[m.ContextId];

                    if (m.MessageType == "ProjectInformation")
                    {
                        var val = m.Payload.ToObject<ProjectMessage>();

                        if (val.GlobalJsonPath != null)
                        {
                            watcher.WatchFile(val.GlobalJsonPath);
                        }

                        var unprocessed = state.ProjectsByFramework.Keys.ToList();

                        foreach (var framework in val.Frameworks)
                        {
                            unprocessed.Remove(framework.FrameworkName);

                            bool exists = true;

                            var project = state.ProjectsByFramework.GetOrAdd(framework.FrameworkName, _ =>
                            {
                                exists = false;
                                return new FrameworkState();
                            });

                            var id = project.ProjectId;

                            if (exists)
                            {
                                continue;
                            }
                            else
                            {
                                var projectInfo = ProjectInfo.Create(
                                        id,
                                        VersionStamp.Create(),
                                        val.Name + "+" + framework.ShortName,
                                        val.Name,
                                        LanguageNames.CSharp);

                                cw.AddProject(projectInfo);
                            }

                            List<ProjectId> references;
                            if (projectReferenceTodos.TryGetValue(id, out references))
                            {
                                lock (references)
                                {
                                    var reference = new Microsoft.CodeAnalysis.ProjectReference(id);

                                    foreach (var referenceId in references)
                                    {
                                        cw.AddProjectReference(referenceId, reference);
                                    }

                                    references.Clear();
                                }
                            }

                        }

                        // Remove old projects
                        foreach (var frameworkName in unprocessed)
                        {
                            FrameworkState frameworkState;
                            state.ProjectsByFramework.TryRemove(frameworkName, out frameworkState);
                            cw.RemoveProject(frameworkState.ProjectId);
                        }
                    }
                    // This is where we can handle messages and update the
                    // language service
                    else if (m.MessageType == "References")
                    {
                        // References as well as the dependency graph information
                        var val = m.Payload.ToObject<ReferencesMessage>();

                        var frameworkState = state.ProjectsByFramework[val.Framework.FrameworkName];
                        var projectId = frameworkState.ProjectId;

                        var metadataReferences = new List<MetadataReference>();
                        var projectReferences = new List<Microsoft.CodeAnalysis.ProjectReference>();

                        var removedFileReferences = frameworkState.FileReferences.ToDictionary(p => p.Key, p => p.Value);
                        var removedRawReferences = frameworkState.RawReferences.ToDictionary(p => p.Key, p => p.Value);
                        var removedProjectReferences = frameworkState.ProjectReferences.ToDictionary(p => p.Key, p => p.Value);

                        foreach (var file in val.FileReferences)
                        {
                            if (removedFileReferences.Remove(file))
                            {
                                continue;
                            }

                            var metadataReference = MetadataReference.CreateFromFile(file);
                            frameworkState.FileReferences[file] = metadataReference;
                            metadataReferences.Add(metadataReference);
                        }

                        foreach (var rawReference in val.RawReferences)
                        {
                            if (removedRawReferences.Remove(rawReference.Key))
                            {
                                continue;
                            }

                            var metadataReference = MetadataReference.CreateFromImage(rawReference.Value);
                            frameworkState.RawReferences[rawReference.Key] = metadataReference;
                            metadataReferences.Add(metadataReference);
                        }

                        foreach (var projectReference in val.ProjectReferences)
                        {
                            if (removedProjectReferences.Remove(projectReference.Path))
                            {
                                continue;
                            }

                            var projectContextId = projects[projectReference.Path];
                            bool exists = true;

                            var otherProject = projectState[projectContextId].ProjectsByFramework.GetOrAdd(projectReference.Framework.FrameworkName,
                                _ =>
                                {
                                    exists = false;
                                    return new FrameworkState();
                                });

                            var projectReferenceId = otherProject.ProjectId;

                            if (exists)
                            {
                                projectReferences.Add(new Microsoft.CodeAnalysis.ProjectReference(projectReferenceId));
                            }
                            else
                            {
                                var references = projectReferenceTodos.GetOrAdd(projectReferenceId, _ => new List<ProjectId>());
                                lock (references)
                                {
                                    references.Add(projectId);
                                }
                            }

                            frameworkState.ProjectReferences[projectReference.Path] = projectReferenceId;
                        }

                        foreach (var reference in metadataReferences)
                        {
                            cw.AddMetadataReference(projectId, reference);
                        }

                        foreach (var projectReference in projectReferences)
                        {
                            cw.AddProjectReference(projectId, projectReference);
                        }

                        foreach (var pair in removedProjectReferences)
                        {
                            cw.RemoveProjectReference(projectId, new Microsoft.CodeAnalysis.ProjectReference(pair.Value));
                            frameworkState.ProjectReferences.Remove(pair.Key);
                        }

                        foreach (var pair in removedFileReferences)
                        {
                            cw.RemoveMetadataReference(projectId, pair.Value);
                            frameworkState.FileReferences.Remove(pair.Key);
                        }

                        foreach (var pair in removedRawReferences)
                        {
                            cw.RemoveMetadataReference(projectId, pair.Value);
                            frameworkState.RawReferences.Remove(pair.Key);
                        }
                    }
                    else if (m.MessageType == "CompilerOptions")
                    {
                        // Configuration and compiler options
                        var val = m.Payload.ToObject<CompilationOptionsMessage>();

                        var projectId = state.ProjectsByFramework[val.Framework.FrameworkName].ProjectId;

                        var options = val.CompilationOptions.CompilationOptions;

                        var specificDiagnosticOptions = options.SpecificDiagnosticOptions
                        .ToDictionary(p => p.Key, p => (ReportDiagnostic)p.Value);

                        var csharpOptions = new CSharpCompilationOptions(
                                outputKind: (OutputKind)options.OutputKind,
                                optimizationLevel: (OptimizationLevel)options.OptimizationLevel,
                                platform: (Platform)options.Platform,
                                generalDiagnosticOption: (ReportDiagnostic)options.GeneralDiagnosticOption,
                                warningLevel: options.WarningLevel,
                                allowUnsafe: options.AllowUnsafe,
                                concurrentBuild: options.ConcurrentBuild,
                                specificDiagnosticOptions: specificDiagnosticOptions
                            );

                        cw.SetCompilationOptions(projectId, csharpOptions);
                    }
                    else if (m.MessageType == "Sources")
                    {
                        // The sources to feed to the language service
                        var val = m.Payload.ToObject<SourcesMessage>();

                        var frameworkState = state.ProjectsByFramework[val.Framework.FrameworkName];
                        var projectId = frameworkState.ProjectId;

                        var unprocessed = new HashSet<string>(frameworkState.Documents.Keys);

                        foreach (var file in val.Files)
                        {
                            if (unprocessed.Remove(file))
                            {
                                continue;
                            }

                            using (var stream = File.OpenRead(file))
                            {
                                var sourceText = SourceText.From(stream, encoding: Encoding.UTF8);
                                var id = DocumentId.CreateNewId(projectId);
                                var version = VersionStamp.Create();

                                frameworkState.Documents[file] = id;

                                var loader = TextLoader.From(TextAndVersion.Create(sourceText, version));

                                cw.AddDocument(DocumentInfo.Create(id, file, loader: loader));
                            }
                        }

                        foreach (var file in unprocessed)
                        {
                            var docId = frameworkState.Documents[file];
                            frameworkState.Documents.Remove(file);
                            cw.RemoveDocument(docId);
                        }
                    }
                };

                // Start the message channel
                queue.Start();

                int contextId = ScanDirectory(hostId,
                                                  projects,
                                                  projectState,
                                                  solutionPath,
                                                  watcher,
                                                  queue,
                                                  0);

                // When there's a file change
                watcher.OnChanged += (changedPath, changeType) =>
                {
                    bool anyProjectChanged = false;

                    foreach (var project in projects)
                    {
                        var directory = Path.GetDirectoryName(project.Key) + Path.DirectorySeparatorChar;

                        if (changedPath.StartsWith(directory))
                        {
                            queue.Post(new Message
                            {
                                ContextId = project.Value,
                                MessageType = "FilesChanged",
                                HostId = hostId
                            });

                            anyProjectChanged = true;

                            foreach (var frameworkState in projectState[project.Value].ProjectsByFramework.Values)
                            {
                                DocumentId documentId;
                                if (frameworkState.Documents.TryGetValue(changedPath, out documentId))
                                {
                                    using (var stream = File.OpenRead(changedPath))
                                    {
                                        var sourceText = SourceText.From(stream, encoding: Encoding.UTF8);
                                        cw.OnDocumentChanged(documentId, sourceText);
                                    }
                                }
                            }
                        }
                    }

                    if (!anyProjectChanged)
                    {
                        if (changeType == WatcherChangeTypes.Created)
                        {
                            contextId = ScanDirectory(hostId,
                                                          projects,
                                                          projectState,
                                                          solutionPath,
                                                          watcher,
                                                          queue,
                                                          contextId);
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
                else if (ki.Key == ConsoleKey.B)
                {
                    foreach (var project in workspace.CurrentSolution.Projects)
                    {
                        Compilation compilation = project.GetCompilationAsync().Result;

                        Console.WriteLine(project.Name);
                        var formatter = new DiagnosticFormatter();
                        foreach (var item in compilation.GetDiagnostics())
                        {
                            Console.WriteLine(formatter.Format(item));
                        }
                    }
                }
                else if (ki.Key == ConsoleKey.P)
                {
                    Console.WriteLine("Actual project IDs");
                    foreach (var state in projectState.Values)
                    {
                        foreach (var project in state.ProjectsByFramework.Values)
                        {
                            Console.WriteLine(project.ProjectId);
                        }
                    }

                    Console.WriteLine("Workspace project IDs");
                    foreach (var projectId in workspace.CurrentSolution.ProjectIds)
                    {
                        Console.WriteLine(projectId);
                    }
                }
                else if (ki.Key == ConsoleKey.I)
                {
                    // Intellisense!
                    //var project = workspace.CurrentSolution.Projects.First();
                    //var document = project.Documents.First();
                    //var sourceText = document.GetTextAsync().Result;
                    //var position = sourceText.Lines.GetPosition(new LinePosition(8, 25));
                    //var model = document.GetSemanticModelAsync().Result;
                    //var symbols = Recommender.GetRecommendedSymbolsAtPosition(model, position, workspace);

                    //foreach (var item in symbols)
                    //{
                    //    Console.WriteLine(item.Name);
                    //}
                }
                else if (ki.Key == ConsoleKey.F)
                {

                }
            }

            ended = true;
            _kreProcess.Kill();
        }

        private static int ScanDirectory(string hostId,
                                         Dictionary<string, int> projects,
                                         Dictionary<int, ProjectState> projectState,
                                         string path,
                                         FileWatcher watcher,
                                         ProcessingQueue queue,
                                         int contextId)
        {
            if (!Directory.Exists(path))
            {
                return contextId;
            }

            foreach (var projectFile in Directory.EnumerateFiles(path, "project.json", SearchOption.AllDirectories))
            {
                if (projects.ContainsKey(projectFile))
                {
                    continue;
                }

                string projectPath = Path.GetDirectoryName(projectFile).TrimEnd(Path.DirectorySeparatorChar);

                // Send an InitializeMessage for each project
                var initializeMessage = new InitializeMessage
                {
                    ProjectFolder = projectPath,
                };

                // Create a unique id for this project
                int projectContextId = contextId++;

                // Create a mapping from path to contextid and back
                projects[projectFile] = projectContextId;
                projectState[projectContextId] = new ProjectState();

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

            return contextId;
        }

        private static void StartRuntime(string runtimePath,
                                         string hostId,
                                         int port,
                                         bool verboseOutput,
                                         Action OnStart)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(runtimePath, "bin", "klr"),
                // CreateNoWindow = true,
                UseShellExecute = false,
                Arguments = String.Format(@"{0} {1} {2} {3}",
                                          Path.Combine(runtimePath, "bin", "lib", "Microsoft.Framework.DesignTimeHost", "Microsoft.Framework.DesignTimeHost.dll"),
                                          port,
                                          Process.GetCurrentProcess().Id,
                                          hostId),
            };

            psi.EnvironmentVariables["KRE_APPBASE"] = Directory.GetCurrentDirectory();

            Console.WriteLine(psi.FileName + " " + psi.Arguments);

            var kreProcess = Process.Start(psi);
            _kreProcess = kreProcess;

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

                if (ended)
                {
                    return;
                }

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
