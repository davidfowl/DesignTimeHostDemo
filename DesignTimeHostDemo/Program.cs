using System;
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
using Microsoft.CodeAnalysis.Text;
using Microsoft.Framework.DesignTimeHost.Models;
using Microsoft.Framework.DesignTimeHost.Models.IncomingMessages;
using Microsoft.Framework.DesignTimeHost.Models.OutgoingMessages;
using Microsoft.Owin.Hosting;
using Newtonsoft.Json.Linq;

namespace DesignTimeHostDemo
{
    class Program
    {
        static void Main(string[] args)
        {
            //Debugger.Launch();
            //Debugger.Break();

            if (args.Length < 1)
            {
                Console.WriteLine("Usage: Omnisharp [-s /path/to/sln] [-p PortNumber]");
                return;
            }
            string applicationRoot = null;
            int? serverPort = null;
            var enumerator = args.GetEnumerator();

            while (enumerator.MoveNext())
            {
                var arg = (string)enumerator.Current;
                if (arg == "-s")
                {
                    enumerator.MoveNext();
                    applicationRoot = Path.GetFullPath((string)enumerator.Current);
                }
                else if (arg == "-p")
                {
                    enumerator.MoveNext();
                    serverPort = Int32.Parse((string)enumerator.Current);
                }
            }

            Go(applicationRoot, serverPort.Value);
        }

        private static void Go(string applicationRoot, int serverPort)
        {
            var workspace = new MyCustomWorkspace();

            Console.WriteLine("OmniSharp server is listening");
            using (WebApp.Start("http://localhost:" + serverPort, new Startup(workspace).Configuration))
            {
                // Initialize ASP.NET 5 projects
                InitializeAspNet5(applicationRoot, workspace);

                Console.WriteLine("Solution has finished loading");

                while (true)
                {
                    Thread.Sleep(5000);
                }
            }
        }

        private static void InitializeAspNet5(string applicationRoot, MyCustomWorkspace workspace)
        {
            var context = new AspNet5Context();
            context.RuntimePath = GetRuntimePath();

            var wh = new ManualResetEventSlim();
            var watcher = new FileWatcher(applicationRoot);
            watcher.OnChanged += (path, changeType) => OnDependenciesChanged(context, path, changeType);

            StartRuntime(context.RuntimePath, context.HostId, context.DesignTimeHostPort, () =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Connect(new IPEndPoint(IPAddress.Loopback, context.DesignTimeHostPort));

                var networkStream = new NetworkStream(socket);

                Console.WriteLine("Connected");

                context.Connection = new ProcessingQueue(networkStream);

                context.Connection.OnReceive += m =>
                {
                    var project = context.Projects[m.ContextId];

                    if (m.MessageType == "ProjectInformation")
                    {
                        var val = m.Payload.ToObject<ProjectMessage>();

                        if (val.GlobalJsonPath != null)
                        {
                            watcher.WatchFile(val.GlobalJsonPath);
                        }

                        var unprocessed = project.ProjectsByFramework.Keys.ToList();

                        foreach (var framework in val.Frameworks)
                        {
                            unprocessed.Remove(framework.FrameworkName);
                            
                            var frameworkState = project.ProjectsByFramework.GetOrAdd(framework.FrameworkName, _ =>
                            {
                                return new FrameworkState();
                            });

                            var id = frameworkState.ProjectId;

                            if (workspace.CurrentSolution.ContainsProject(id))
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

                                workspace.AddProject(projectInfo);
                            }

                            lock (frameworkState.PendingProjectReferences)
                            {
                                var reference = new Microsoft.CodeAnalysis.ProjectReference(id);

                                foreach (var referenceId in frameworkState.PendingProjectReferences)
                                {
                                    workspace.AddProjectReference(referenceId, reference);
                                }

                                frameworkState.PendingProjectReferences.Clear();
                            }

                        }

                        // Remove old projects
                        foreach (var frameworkName in unprocessed)
                        {
                            FrameworkState frameworkState;
                            project.ProjectsByFramework.TryRemove(frameworkName, out frameworkState);
                            workspace.RemoveProject(frameworkState.ProjectId);
                        }
                    }
                    // This is where we can handle messages and update the
                    // language service
                    else if (m.MessageType == "References")
                    {
                        // References as well as the dependency graph information
                        var val = m.Payload.ToObject<ReferencesMessage>();

                        var frameworkState = project.ProjectsByFramework[val.Framework.FrameworkName];
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

                            var projectReferenceContextId = context.ProjectContextMapping[projectReference.Path];
                            bool exists = true;

                            var referencedProject = context.Projects[projectReferenceContextId].ProjectsByFramework.GetOrAdd(projectReference.Framework.FrameworkName,
                                _ =>
                                {
                                    exists = false;
                                    return new FrameworkState();
                                });

                            var projectReferenceId = referencedProject.ProjectId;

                            if (exists)
                            {
                                projectReferences.Add(new Microsoft.CodeAnalysis.ProjectReference(projectReferenceId));
                            }
                            else
                            {
                                lock (referencedProject.PendingProjectReferences)
                                {
                                    referencedProject.PendingProjectReferences.Add(projectId);
                                }
                            }
                            referencedProject.ProjectDependeees.Add(project.Path);

                            frameworkState.ProjectReferences[projectReference.Path] = projectReferenceId;
                        }

                        foreach (var reference in metadataReferences)
                        {
                            workspace.AddMetadataReference(projectId, reference);
                        }

                        foreach (var projectReference in projectReferences)
                        {
                            workspace.AddProjectReference(projectId, projectReference);
                        }

                        foreach (var pair in removedProjectReferences)
                        {
                            workspace.RemoveProjectReference(projectId, new Microsoft.CodeAnalysis.ProjectReference(pair.Value));
                            frameworkState.ProjectReferences.Remove(pair.Key);
                        }

                        foreach (var pair in removedFileReferences)
                        {
                            workspace.RemoveMetadataReference(projectId, pair.Value);
                            frameworkState.FileReferences.Remove(pair.Key);
                        }

                        foreach (var pair in removedRawReferences)
                        {
                            workspace.RemoveMetadataReference(projectId, pair.Value);
                            frameworkState.RawReferences.Remove(pair.Key);
                        }
                    }
                    else if (m.MessageType == "CompilerOptions")
                    {
                        // Configuration and compiler options
                        var val = m.Payload.ToObject<CompilationOptionsMessage>();

                        var projectId = project.ProjectsByFramework[val.Framework.FrameworkName].ProjectId;

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

                        var parseOptions = new CSharpParseOptions(val.CompilationOptions.LanguageVersion,
                                                                  preprocessorSymbols: val.CompilationOptions.Defines);

                        workspace.SetCompilationOptions(projectId, csharpOptions);
                        workspace.SetParseOptions(projectId, parseOptions);
                    }
                    else if (m.MessageType == "Sources")
                    {
                        // The sources to feed to the language service
                        var val = m.Payload.ToObject<SourcesMessage>();

                        var frameworkState = project.ProjectsByFramework[val.Framework.FrameworkName];
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
                                workspace.AddDocument(DocumentInfo.Create(id, file, filePath: file, loader: loader));
                            }
                        }

                        foreach (var file in unprocessed)
                        {
                            var docId = frameworkState.Documents[file];
                            frameworkState.Documents.Remove(file);
                            workspace.RemoveDocument(docId);
                        }

                        frameworkState.Loaded = true;
                    }

                    if (project.ProjectsByFramework.Values.All(p => p.Loaded))
                    {
                        wh.Set();
                    }
                };

                // Start the message channel
                context.Connection.Start();

                // Scan for the projects
                ScanForAspNet5Projects(context, applicationRoot, watcher);
            });

            wh.Wait();
        }

        private static void OnDependenciesChanged(AspNet5Context context, string path, WatcherChangeTypes changeType)
        {
            // A -> B -> C
            // If C changes, trigger B and A

            TriggerDependeees(context, path);
        }

        private static void TriggerDependeees(AspNet5Context context, string path)
        {
            var seen = new HashSet<string>();
            var results = new HashSet<int>();
            var stack = new Stack<string>();

            stack.Push(path);

            while (stack.Count > 0)
            {
                var projectPath = stack.Pop();

                if (!seen.Add(projectPath))
                {
                    continue;
                }

                int contextId;
                if (context.ProjectContextMapping.TryGetValue(projectPath, out contextId))
                {
                    results.Add(contextId);

                    foreach (var frameworkState in context.Projects[contextId].ProjectsByFramework.Values)
                    {
                        foreach (var dependee in frameworkState.ProjectDependeees)
                        {
                            stack.Push(dependee);
                        }
                    }
                }
            }

            foreach (var contextId in results)
            {
                var message = new Message();
                message.HostId = context.HostId;
                message.ContextId = contextId;
                message.MessageType = "FilesChanged";
                context.Connection.Post(message);
            }
        }

        private static void ScanForAspNet5Projects(AspNet5Context context, string applicationRoot, FileWatcher watcher)
        {
            Console.WriteLine("Scanning {0} for ASP.NET 5 projects", applicationRoot);

            foreach (var projectFile in Directory.EnumerateFiles(applicationRoot, "project.json", SearchOption.AllDirectories))
            {
                int contextId;
                if (!context.TryAddProject(projectFile, out contextId))
                {
                    continue;
                }

                string projectPath = Path.GetDirectoryName(projectFile).TrimEnd(Path.DirectorySeparatorChar);

                // Send an InitializeMessage for each project
                var initializeMessage = new InitializeMessage
                {
                    ProjectFolder = projectPath,
                };

                // Initialize this project
                context.Connection.Post(new Message
                {
                    ContextId = contextId,
                    MessageType = "Initialize",
                    Payload = JToken.FromObject(initializeMessage),
                    HostId = context.HostId
                });

                watcher.WatchFile(projectFile);
                Console.WriteLine("Detected {0}", projectFile);
            }
        }

        private static void StartRuntime(string runtimePath,
                                         string hostId,
                                         int port,
                                         Action OnStart)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(runtimePath, "bin", "klr"),
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                Arguments = string.Format(@"{0} {1} {2} {3}",
                                          Path.Combine(runtimePath, "bin", "lib", "Microsoft.Framework.DesignTimeHost", "Microsoft.Framework.DesignTimeHost.dll"),
                                          port,
                                          Process.GetCurrentProcess().Id,
                                          hostId),
            };

            psi.EnvironmentVariables["KRE_APPBASE"] = Directory.GetCurrentDirectory();

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

                StartRuntime(runtimePath, hostId, port, OnStart);
            };

            OnStart();
        }

        private static Task ConnectAsync(Socket socket, IPEndPoint endPoint)
        {
            return Task.Factory.FromAsync((cb, state) => socket.BeginConnect(endPoint, cb, state), ar => socket.EndConnect(ar), null);
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

        public class AspNet5Context
        {
            private int _contextId;

            public string RuntimePath { get; set; }

            public string HostId { get; private set; }

            public int DesignTimeHostPort { get; set; }

            public Dictionary<string, int> ProjectContextMapping { get; set; }

            public Dictionary<int, ProjectState> Projects { get; set; }

            public ProcessingQueue Connection { get; set; }

            public AspNet5Context()
            {
                HostId = Guid.NewGuid().ToString();
                DesignTimeHostPort = 1334;
                ProjectContextMapping = new Dictionary<string, int>();
                Projects = new Dictionary<int, ProjectState>();
            }

            public bool TryAddProject(string projectFile, out int contextId)
            {
                contextId = -1;
                if (ProjectContextMapping.ContainsKey(projectFile))
                {
                    return false;
                }

                contextId = ++_contextId;

                // Create a mapping from path to contextid and back
                ProjectContextMapping[projectFile] = contextId;
                Projects[contextId] = new ProjectState
                {
                    Path = projectFile
                };

                return true;
            }
        }
    }
}
