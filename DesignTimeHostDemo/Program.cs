using System;
using System.Linq;
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
using Microsoft.CodeAnalysis.Recommendations;
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

            Workspace workspace = null;
            var projects = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var projectState = new Dictionary<int, ProjectState>();
            var documents = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
            var projectReferenceTodos = new ConcurrentDictionary<ProjectId, List<ProjectId>>();

            StartRuntime(runtimePath, hostId, port, showRuntimeOutput, () =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                socket.Connect(new IPEndPoint(IPAddress.Loopback, port));

                var networkStream = new NetworkStream(socket);

                Console.WriteLine("Connected");

                var solutionPath = applicationRoot;
                var watcher = new FileWatcher(solutionPath);
                var queue = new ProcessingQueue(networkStream);

                var cw = new MyCustomWorkspace();

                var solution = cw.AddSolution(SolutionInfo.Create(
                    SolutionId.CreateNewId(),
                    VersionStamp.Create()));

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

                        var unprocessed = state.WorkspaceProjects.Keys.ToList();

                        foreach (var framework in val.Frameworks)
                        {
                            unprocessed.Remove(framework.FrameworkName);

                            bool exists = true;

                            var id = state.WorkspaceProjects.GetOrAdd(framework.FrameworkName, _ =>
                            {
                                exists = false;
                                return ProjectId.CreateNewId();
                            });

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

                                solution = solution.AddProject(projectInfo);
                            }

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

                        // Remove old projects
                        foreach (var frameworkName in unprocessed)
                        {
                            ProjectId id;
                            state.WorkspaceProjects.TryRemove(frameworkName, out id);
                            solution = solution.RemoveProject(id);
                        }
                    }
                    // This is where we can handle messages and update the
                    // language service
                    else if (m.MessageType == "References")
                    {
                        // References as well as the dependency graph information
                        var val = m.Payload.ToObject<ReferencesMessage>();

                        var projectId = state.WorkspaceProjects[val.Framework.FrameworkName];

                        var project = solution.GetProject(projectId);

                        var metadataReferences = new List<MetadataReference>();
                        var projectReferences = new List<Microsoft.CodeAnalysis.ProjectReference>();

                        // TODO: Cache these
                        foreach (var file in val.FileReferences)
                        {
                            metadataReferences.Add(MetadataReference.CreateFromFile(file));
                        }

                        foreach (var rawReference in val.RawReferences)
                        {
                            metadataReferences.Add(MetadataReference.CreateFromImage(rawReference.Value));
                        }

                        foreach (var projectReference in val.ProjectReferences)
                        {
                            var projectContextId = projects[projectReference.Path];
                            bool exists = true;
                            var projectReferenceId = projectState[projectContextId].WorkspaceProjects.GetOrAdd(projectReference.Framework.FrameworkName,
                                _ =>
                                {
                                    exists = false;
                                    return ProjectId.CreateNewId();
                                });

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
                        }

                        // This seems to be less inefficient than it needs to be, but it works
                        var newProject = project.WithMetadataReferences(metadataReferences)
                                                .WithProjectReferences(projectReferences);

                        //var changes = newProject.GetChanges(project);
                        //var addedRefs = changes.GetAddedMetadataReferences().ToList();
                        //var removedRefs = changes.GetRemovedMetadataReferences().ToList();
                        //var addedPRefs = changes.GetAddedProjectReferences().ToList();
                        //var removedPRefs = changes.GetRemovedProjectReferences().ToList();

                        solution = newProject.Solution;
                    }
                    else if (m.MessageType == "CompilerOptions")
                    {
                        // Configuration and compiler options
                        var val = m.Payload.ToObject<CompilationOptionsMessage>();

                        var projectId = state.WorkspaceProjects[val.Framework.FrameworkName];

                        var project = solution.GetProject(projectId);
                        var newProject = project;

                        var options = val.CompilationOptions.CompilationOptions;

                        var specificDiagnosticOptions = options.SpecificDiagnosticOptions
                        .ToDictionary(p => p.Key, p => (ReportDiagnostic)p.Value);

                        newProject = newProject.WithCompilationOptions(
                            new CSharpCompilationOptions(
                                outputKind: (OutputKind)options.OutputKind,
                                optimizationLevel: (OptimizationLevel)options.OptimizationLevel,
                                platform: (Platform)options.Platform,
                                generalDiagnosticOption: (ReportDiagnostic)options.GeneralDiagnosticOption,
                                warningLevel: options.WarningLevel,
                                allowUnsafe: options.AllowUnsafe,
                                concurrentBuild: options.ConcurrentBuild,
                                specificDiagnosticOptions: specificDiagnosticOptions
                            ));

                        solution = newProject.Solution;
                    }
                    else if (m.MessageType == "Sources")
                    {
                        // The sources to feed to the language service
                        var val = m.Payload.ToObject<SourcesMessage>();

                        var projectId = state.WorkspaceProjects[val.Framework.FrameworkName];

                        var project = solution.GetProject(projectId);

                        foreach (var file in val.Files)
                        {
                            using (var stream = File.OpenRead(file))
                            {
                                var sourceText = SourceText.From(stream, encoding: Encoding.UTF8);
                                var id = DocumentId.CreateNewId(projectId);
                                var version = VersionStamp.Create();

                                documents[file] = id;

                                var loader = TextLoader.From(TextAndVersion.Create(sourceText, version));
                                solution = solution.AddDocument(DocumentInfo.Create(id, file, loader: loader));
                            }
                        }
                    }

                    bool changed = cw.TryApplyChanges(solution);

                    Console.WriteLine(m.MessageType + " changed? => " + changed);
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
                        foreach (var id in state.WorkspaceProjects.Values)
                        {
                            Console.WriteLine(id);
                        }
                    }

                    Console.WriteLine("Workspace project IDs");
                    foreach (var projectId in workspace.CurrentSolution.ProjectIds)
                    {
                        Console.WriteLine(projectId);
                    }
                }
            }
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

    public class MyCustomWorkspace : CustomWorkspace
    {
        public override bool CanApplyChange(ApplyChangesKind feature)
        {
            return true;
        }

        public override bool TryApplyChanges(Solution newSolution)
        {
            var applied = base.TryApplyChanges(newSolution);

            SetCurrentSolution(newSolution);

            return applied;
        }
    }
}
