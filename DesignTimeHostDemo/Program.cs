using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DesignTimeHostDemo.Test;
using Microsoft.Framework.DesignTimeHost.Models;
using Microsoft.Framework.DesignTimeHost.Models.IncomingMessages;
using Newtonsoft.Json.Linq;

namespace DesignTimeHostDemo
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: DesignTimeHostDemo [runtimePath] [solutionPath] [optional switch: -startuptest]");
                return;
            }

            string runtimePath = args[0];
            string applicationRoot = args[1];
            bool startupTest = false;

            if (args.Length > 2)
            {
                if (!string.IsNullOrEmpty(args[2]) && args[2].Equals("-startuptest", StringComparison.OrdinalIgnoreCase))
                {
                    startupTest = true;
                }
            }

            Go(runtimePath, applicationRoot, startupTest);
        }

        private static void Go(string runtimePath, string applicationRoot, bool startupTest)
        {
            var hostId = Guid.NewGuid().ToString();
            var port = 1334;

            // Show runtime output
            var showRuntimeOutput = true;

            // test readiness watcher
            var testReadinessWatcher = new HostReadinessWatcher();

            StartRuntime(runtimePath, hostId, port, showRuntimeOutput, () =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                socket.Connect(new IPEndPoint(IPAddress.Loopback, port));

                var networkStream = new NetworkStream(socket);

                Console.WriteLine("Connected");

                var mapping = new Dictionary<int, string>();
                var messages = new ConcurrentDictionary<int, List<Message>>();
                var queue = new ProcessingQueue(networkStream);

                queue.OnReceive += m =>
                {
                    testReadinessWatcher.Ping();

                    // Get the project associated with this message
                    var projectPath = mapping[m.ContextId];

                    // This is where we can handle messages and update the
                    // language service
                    if (m.MessageType == "References")
                    {
                        // References as well as the dependency graph information
                        // var val = m.Payload.ToObject<ReferencesMessage>();
                    }
                    else if (m.MessageType == "Diagnostics")
                    {
                        // Errors and warnings
                        // var val = m.Payload.ToObject<DiagnosticsMessage>();
                    }
                    else if (m.MessageType == "Configurations")
                    {
                        // Configuration and compiler options
                        // var val = m.Payload.ToObject<ConfigurationsMessage>();
                    }
                    else if (m.MessageType == "Sources")
                    {
                        // The sources to feed to the language service
                        // var val = m.Payload.ToObject<SourcesMessage>();

                        List<Message> messageList;
                        if (!messages.TryGetValue(m.ContextId, out messageList))
                        {
                            messageList = new List<Message>();
                            messages[m.ContextId] = messageList;
                        }

                        messageList.Add(m);
                    }
                };

                // Start the message channel
                queue.Start();

                var solutionPath = applicationRoot;
                var watcher = new FileWatcher(solutionPath);
                var projects = new Dictionary<string, int>();
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

            if (!startupTest)
            {
                // if test module is not given, running in the console mode
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
            else
            {
                testReadinessWatcher.Start();
                testReadinessWatcher.Wait();
                Console.WriteLine("Ready to test");

                // Press anything to continue
                while (true)
                {
                    var ki = Console.ReadKey(true);
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
