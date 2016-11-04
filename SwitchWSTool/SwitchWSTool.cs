using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using Microsoft.TeamFoundation.Build.Client;
using Microsoft.TeamFoundation.Client;
using Microsoft.TeamFoundation.VersionControl.Client;
using Timer = System.Timers.Timer;

namespace SwitchWS
{
    class SwitchWS
    {
        private static string myLogFile = ConfigurationSettings.AppSettings["LogFile"];
        private static Dictionary<string, string> myArgsSorted = new Dictionary<string, string>();
        private const string myDeployedToolsPath = @"$/Tools/DeployedTools";
        private const string myModulesFullPath = @"$/syngo.net/Modules/";

        private static Timer myTimer;
        private static DateTime myStopTime;
        private static string myTfsServer = ConfigurationManager.AppSettings["TfsServer"];

        static void Main(string[] args)
        {
            // Parsing argumets
            ParseArgs(args);

            // Needs to be defined workspace otherwise return
            if (!myArgsSorted.ContainsKey("-ws") || myArgsSorted.ContainsKey("-h"))
            {
                DisplayHelp();
                return;
            }

            if (myArgsSorted.ContainsKey("-d"))
            {
                Debugger.Launch();
            }

            // When an user specify -m argument only modules will be generated
            var skipSourceDllsUpdate = myArgsSorted.ContainsKey("-m");

            // Getting basic parameters from command like/configuration file
            string branch = ConfigurationSettings.AppSettings["Branch"].Split(new []{'/'}).Last();
            string branchFullPath = ConfigurationSettings.AppSettings["Branch"];
            string workspaceRoot = ConfigurationSettings.AppSettings["WorkspaceRoot"];
            string workspaceName = myArgsSorted.ContainsKey("-ws") ? myArgsSorted["-ws"] : null;
            string workspacePath = workspaceRoot + "\\" + workspaceName;
            string configuration = ConfigurationSettings.AppSettings["Configuration"];
            string platform = ConfigurationSettings.AppSettings["Platform"];
            string workspaceDll = workspacePath + "\\" + branch + "\\bin\\" + platform + "\\" + configuration;

            if (!string.IsNullOrEmpty(myLogFile) && !File.Exists(myLogFile) )
            {
                Directory.CreateDirectory(Path.GetDirectoryName(myLogFile));
            }

            // Establishing connection to tfs server and getting Build_definition detailes
            TfsTeamProjectCollection tfs = new TfsTeamProjectCollection(new Uri(myTfsServer));
            var buildServer = (IBuildServer)tfs.GetService(typeof(IBuildServer));
            var buildDefinition = buildServer.GetBuildDefinition("syngo.net", ConfigurationSettings.AppSettings["Build_definition"], QueryOptions.Definitions);
            var buildDetSpec = buildServer.CreateBuildDetailSpec(buildDefinition);
            var buildDefSpec = buildServer.CreateBuildDefinitionSpec(buildDefinition);

            WriteTo("SwitchWS started at " + DateTime.Now);

            // Looking for argument -b which specify build ID otherwise last succesfull will be taken
            string buildID = myArgsSorted.ContainsKey("-b") ? myArgsSorted["-b"] : null;
            IBuildDetail buildDetail;
            string buildLocation;
            if (buildID == null)
            {
                WriteTo("Looking for feasible build...");
                GetBuildDetail(buildDetSpec, buildServer, buildDefinition, out buildLocation, out buildDetail);
            }
            else
            {
                buildLocation = ConfigurationSettings.AppSettings["Build_location"] + "\\" + buildID + "\\" +
                                ConfigurationSettings.AppSettings["Platform"] + "\\" +
                                ConfigurationSettings.AppSettings["Configuration"];
                if (!Directory.Exists(buildLocation))
                {
                    WriteTo("For the BuildId " + buildID + " was not able to find dlls store in the following location" + buildLocation);
                    return;
                }
                buildDetail = buildServer.GetBuild(buildDefSpec, buildID, null, QueryOptions.All);
            }

            if (buildDetail == null)
            {
                Console.WriteLine("Could not find build detailes" + Environment.NewLine);
                return;
            }

            // Looking for relevant changeset
            var sourceChangeset = Convert.ToInt32(buildDetail.SourceGetVersion.Replace("C", ""));
            WriteTo("Found relevant changeset " + sourceChangeset);

            // Getting workspace. If it doesn't exist create
            VersionControlServer versionControl = (VersionControlServer)tfs.GetService(typeof(VersionControlServer));
            Workspace workSpace;
            try
            {
                workSpace = versionControl.GetWorkspace(workspacePath);
            }
            catch
            {
                WriteTo("Can't find mappings rule for the workspace path: " + workspacePath);
                WriteTo("Would you like to create workspace " + workspaceName + " with applied mapping rule (Y/N)?");
                WriteTo("Source control folder: " + branchFullPath + " Local folder: " + workspacePath);
                WriteTo("with applied mapping rule (Y/N)?");
                if (Console.ReadKey().Key.ToString().ToLower() == "y")
                {
                    Console.WriteLine();
                    workSpace = versionControl.CreateWorkspace(workspaceName, Environment.UserName, "Created by SwitchWS",
                                                               new[]
                                                                   {
                                                                       new WorkingFolder(branchFullPath.Replace(branch, ""), workspacePath)
                                                                   });
                }
                else
                {
                    Console.WriteLine();
                    WriteTo(Environment.NewLine + "Cannot continue without appropriate workspace..." + Environment.NewLine);
                    return;
                }
            }

            // Collect bundle names which shall be downloaded. If config file doesn't contain any, all of them will be downloaded
            string bundles = ConfigurationSettings.AppSettings["Bundles"];
            string[] bundlesArray;
            string message;
            if (bundles == "")
            {
                bundlesArray = new[] { branchFullPath };
                message = "Getting sources for all bundles";
            }
            else
            {
                bundlesArray = ConfigurationSettings.AppSettings["Bundles"].Split(',');
                message = "Getting sources for " + bundles + " bundles";
                for (int i = 0; i < bundlesArray.Count(); i++)
                {
                    bundlesArray[i] = branchFullPath + "/" + bundlesArray[i];
                }
            }

            Process process;
            if (!skipSourceDllsUpdate)
            {
                // Force sources download
                WriteTo(message + Environment.NewLine);
                GetStatus status = workSpace.Get(bundlesArray, new ChangesetVersionSpec(sourceChangeset),
                    RecursionType.Full, GetOptions.Overwrite);
                WriteTo("Result of getting sources:");
                WriteTo("Number haveResolvableWarnings: " + status.HaveResolvableWarnings);
                WriteTo("Number noActionNeeded        : " + status.NoActionNeeded);
                WriteTo("Number conflicts             : " + status.NumConflicts);
                WriteTo("Number failures              : " + status.NumFailures);
                WriteTo("Number operations            : " + status.NumOperations);
                WriteTo("Number updateds              : " + status.NumUpdated);
                WriteTo("Number warnings              : " + status.NumWarnings + Environment.NewLine);

                // Execution dlls download from drop location
                WriteTo("Downloading dlls from dropfolder...");
                ExecuteRobocopy(buildLocation, workspaceDll, out process);
            }

            // If configuration file contains Modules parameter trigger Modules update based on the Globals/VersionInformation
            var modules = ConfigurationSettings.AppSettings["Modules"].Split(',');
            if (modules.First() != "")
            {
                WriteTo(Environment.NewLine + "Updating modules...");
                var deployedToolsDir = workspacePath + "\\" + myDeployedToolsPath.Split(new[] { '/' }).Last();
                // Create mapping for Deployment tools and download them
                workSpace.CreateMapping(new WorkingFolder(myDeployedToolsPath, deployedToolsDir));
                workSpace.Get(new[] { myDeployedToolsPath }, VersionSpec.Latest, RecursionType.Full, GetOptions.Overwrite);


                foreach (var modulePathWithWhitespace in modules)
                {
                    var modulePath = modulePathWithWhitespace.TrimStart();
                    var module = modulePath.Split(new[] { "$/syngo.net/Modules/" }, StringSplitOptions.RemoveEmptyEntries).Last().Split(new[] { '/' }).First();

                    var moduleRoot = workspacePath + "\\" + module;
                    // Create mapping for current module
                    workSpace.CreateMapping(new WorkingFolder(myModulesFullPath + module, moduleRoot));

                    var versionInformaitonDirectory = new DirectoryInfo(workspacePath + "\\" + branch + "\\_Globals\\VersionInformation");
                    var moduleVersionFile = versionInformaitonDirectory.GetFiles("*" + module + "*.xml").First();
                    var moduleVersionDoc = new XmlDocument();
                    moduleVersionDoc.Load(moduleVersionFile.FullName);
                    // Remember module version
                    var moduleVersion = moduleVersionDoc.GetElementsByTagName("Version").Item(0).InnerText;
                    string moduleVersionChs = "";

                    var moduleVersionFilePath = modulePath + "/_Globals/VersionInformation/" +
                                                moduleVersionFile.FullName.Split('\\').Last();

                    var changesetHistory = versionControl.QueryHistory(moduleVersionFilePath, VersionSpec.Latest, 0, RecursionType.None, null,
                        VersionSpec.ParseSingleSpec("380000", null), VersionSpec.Latest, 20, true, false);
                    foreach (Changeset changeset in changesetHistory)
                    {
                        var chsId = changeset.ChangesetId.ToString();
                        var item = versionControl.GetItem(moduleVersionFilePath,
                            VersionSpec.ParseSingleSpec(chsId, null), 0);
                        using (var st = item.DownloadFile())
                        {
                            using (var str = new StreamReader(st))
                            {
                                var file = str.ReadToEnd();
                                if (file.Contains(moduleVersion))
                                {
                                    // Remember changeset ID of current module
                                    moduleVersionChs = chsId;
                                    break;
                                }
                            }
                        }  
                    }

                    if (string.IsNullOrEmpty(moduleVersionChs))
                    {
                        WriteTo(string.Format("Found problem with getting module version of {0} bundle. Process will be skipped...", module) + Environment.NewLine);
                        continue;
                    }

                    WriteTo(string.Format("Found relevant changeset {0} for module {1} with version {2}", moduleVersionChs, module, moduleVersion));

                    // Getting sources for module under changeset 'moduleVersionChs'
                    WriteTo(string.Format("Getting sources for module: {0}", module) + Environment.NewLine);
                    workSpace.Get(new[] { modulePath }, new ChangesetVersionSpec(moduleVersionChs), RecursionType.Full, GetOptions.GetAll);

                    // this has to be discussed what next
                    //var repoRootTmp = bundlePath.Replace("$/syngo.net/Modules", workspacePath).Replace('/', '\\');
                    //var moduleSpecLocation = repoRootTmp + "\\BundleSpec.xml";
                    //var moduleSolLocation = repoRootTmp + "\\" + bundle + ".sln";
                    //var repoRoot = repoRootTmp.Replace(repoRootTmp.Split('\\').Last(), "");

                    //// Downloading dependencies for module bundle
                    //var arguments = string.Format("-BundleSpec {0} -WorkspaceRoot {1}", moduleSpecLocation, repoRoot);
                    //WriteTo(string.Format(Environment.NewLine + "Download dependencies for bundle: {0}", bundle));
                    //ExecuteCommand(deployedToolsDir + "\\DependencyManagement.Console.exe", arguments, true, true, out process);
                    //WriteTo(process.StandardOutput.ReadToEnd());
                    //process.WaitForExit();

                    //// Compile solution
                    //arguments = string.Format("{0} /m /p:RunCodeAnalysis=false /p:Configuration={1},Platform={2}", moduleSolLocation, configuration, platform);
                    //WriteTo(string.Format(Environment.NewLine + "Build solution: {0}", moduleSolLocation));
                    //ExecuteCommand("C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\MSBuild.exe", arguments, true, true, out process);
                    //WriteTo(process.StandardOutput.ReadToEnd());
                    //process.WaitForExit();
                    
                    //if (!Directory.Exists(workspaceDll))
                    //{
                    //    Directory.CreateDirectory(workspaceDll);
                    //}

                    //// Copying pdb and dll files from module location to workspace dll root location
                    //var moduleBin = repoRoot + "\\bin";
                    //foreach (var file in Directory.GetFiles(moduleBin, "*.pdb", SearchOption.AllDirectories))
                    //{
                    //    var sourceFilePath = file;
                    //    var fileName = file.Split('\\').Last();
                    //    var destFilePath = workspaceDll + "\\" + fileName;
                    //    var sourceFilePathDll = sourceFilePath.Replace(".pdb", ".dll");
                    //    var destFilePathDll = destFilePath.Replace(".pdb", ".dll");
                    //    if (File.Exists(sourceFilePathDll))
                    //    {
                    //        WriteTo(string.Format("Copying file from {0} to {1} location", sourceFilePath, destFilePath));
                    //        File.Copy(sourceFilePath, destFilePath, true);
                    //        WriteTo(string.Format("Copying file from {0} to {1} location", sourceFilePathDll, destFilePathDll));
                    //        File.Copy(sourceFilePathDll, destFilePathDll, true);
                    //    }
                    //}
                }
            }
            else
            {
                WriteTo(Environment.NewLine + "\'BundlesForCompile\' in switchws.exe.config was not defined therefore modules compilation will be skipped");
            }

            WriteTo( Environment.NewLine + "SwitchWS finished at " + DateTime.Now + Environment.NewLine);
        }

        /// <summary>
        /// Execution robocopy command
        /// </summary>
        /// <param name="buildLocation"></param>
        /// <param name="workspaceLocation"></param>
        /// <param name="process"></param>
        private static void ExecuteRobocopy(string buildLocation, string workspaceLocation, out Process process)
        {
            const string robocopyCmd = @"c:\Windows\System32\Robocopy.exe";
            string arguments = buildLocation + " " + workspaceLocation + " " + @"/PURGE /E /NP /R:5 /W:2 /mt /NFL /NDL";

            WriteTo(string.Format("Executing cmd {0} {1}",robocopyCmd, arguments));

            ExecuteCommand(robocopyCmd, arguments, false, true, out process);

            if (GettingFilesCount(workspaceLocation) == 0)
            {
                Console.Write("\r0% downloaded");
                var sourceFilesCount = GettingFilesCount(buildLocation);
                RobocopyProgressState(sourceFilesCount, workspaceLocation);
            }
            else
            {
                WriteTo("Cannot evaluate download progress because you are updating existing workspace.");
                WriteTo("Wait please...");
            }
            
            var result = process.StandardOutput.ReadToEnd().Replace("\r100%  ", "");
            process.WaitForExit();
            WriteTo("\r100% downloaded");
            WriteToLog(result);
        }

        /// <summary>
        /// Execution command
        /// </summary>
        /// <param name="arguments"></param>
        /// <param name="process"></param>
        /// <param name="executionFile"></param>
        private static void ExecuteCommand(string executionFile, string arguments, bool loggingEnabled, bool redirectStOut, out Process process)
        {
            process = new Process();
            process.StartInfo.FileName = executionFile;
            process.StartInfo.RedirectStandardOutput = redirectStOut;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.Arguments = arguments;
            if (loggingEnabled)
            {
                WriteTo(string.Format("Executing cmd {0} {1}", executionFile, process.StartInfo.Arguments));
            }
            process.Start();
        }

        /// <summary>
        /// This method returns build detailes
        /// </summary>
        /// <param name="buildDetSpec"></param>
        /// <param name="buildServer"></param>
        /// <param name="buildDefinition"></param>
        /// <param name="buildLocation"></param>
        /// <param name="buildDetail"></param>
        private static void GetBuildDetail(IBuildDetailSpec buildDetSpec,IBuildServer buildServer, IBuildDefinition buildDefinition, out string buildLocation, out IBuildDetail buildDetail)
        {
            buildLocation = null;
            buildDetail = null;
            for (int i = 0; i < 15; i++)
            {
                int day = DateTime.Now.AddDays(-i).Day;
                int month = DateTime.Now.AddDays(-i).Month;
                int year = DateTime.Now.AddDays(-i).Year;
                buildDetSpec.MinFinishTime = new DateTime(year, month, day, 0, 0, 0);
                buildDetSpec.MaxFinishTime = new DateTime(year, month, day, 23, 59, 59);
                var buildsDetail = buildServer.QueryBuilds(buildDetSpec).Builds;
                foreach (var buildDet in buildsDetail.Reverse())
                {
                    buildLocation = ConfigurationSettings.AppSettings["Build_location"] + "\\" +
                                    buildDet.BuildNumber + "\\" +
                                    ConfigurationSettings.AppSettings["Platform"] + "\\" +
                                    ConfigurationSettings.AppSettings["Configuration"];
                    if (Directory.Exists(buildLocation) &&
                        buildDet.BuildNumber.Contains(buildDefinition.Name) &&
                        (buildDet.Status == BuildStatus.PartiallySucceeded ||
                         buildDet.Status == BuildStatus.Succeeded))
                    {
                        buildDetail = buildDet;
                        WriteTo("Found one: " + buildDet.BuildNumber);
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Display help
        /// </summary>
        private static void DisplayHelp()
        {
            Console.WriteLine("Syntax: switchws.exe -ws [workspace] -b [build]");
            Console.WriteLine("e.g.    switchws.exe -ws TempWorkspace -b VIA.IPV.Fatra.NB_20121120.1");
            Console.WriteLine("option (mandatory):");
            Console.WriteLine("                   -ws   : Worskpace");
            Console.WriteLine("option (optional) :");
            Console.WriteLine("                   -b    : Build from which shall be downloaded dlls");
            Console.WriteLine("                   -d    : Debug mode. Attaching debugger wil be forced");
            Console.WriteLine("                   -m    : Only modules will be compiled");
            Console.WriteLine("                   -h    : Display this help description");       
        }

        /// <summary>
        /// Arguments parser
        /// </summary>
        /// <param name="args"></param>
        private static void ParseArgs(string[] args)
        {
            for(int i= 0; i < args.Count(); i++)
            {
                if (args[i].Contains("-"))
                {
                    if (i < (args.Count() - 1) && !args[i + 1].Contains("-"))
                    {
                        myArgsSorted[args[i]] = args[i + 1];
                        i++;
                    }
                    else
                    {
                        myArgsSorted[args[i]] = String.Empty;
                    }
                }
            }
        }

        /// <summary>
        /// Write a message to command line and log file
        /// </summary>
        /// <param name="message"></param>
        private static void WriteTo(string message)
        {
            Console.WriteLine(message);
            WriteToLog(message);
        }

        /// <summary>
        /// Write a message to log file
        /// </summary>
        /// <param name="message"></param>
        private static void WriteToLog(string message)
        {
            if (myLogFile != "")
            {
                var log = File.AppendText(myLogFile);
                log.WriteLine(message);
                log.Close();
            }
        }

        private static void RobocopyProgressState(float sourceFilesCount, string targetDirectory)
        {
            float state;
            do
            {
                var stopTime = DateTime.Now.AddSeconds(30);
                var targetFilesCount = GettingFilesCount(targetDirectory);
                state = (targetFilesCount / sourceFilesCount) * 100;
                Console.Write("\r{0}% downloaded", Math.Floor(state));
                while (!Convert.ToInt16(state).Equals(100))
                {
                    if (DateTime.Now > stopTime) break;
                    Thread.Sleep(5000);
                }

            } while (state < 100);
        }

        private static float GettingFilesCount(string directory)
        {
            if (!Directory.Exists(directory)) return 0;
            Process process;
            var arguments = string.Format("/C dir {0} /B /S", directory);
            ExecuteCommand("cmd.exe", arguments, false, true, out process);

            var output = process.StandardOutput.ReadToEnd();
            var filesCount = output.Split(new[] { "\r" }, StringSplitOptions.RemoveEmptyEntries).Count();
            process.WaitForExit();
            return filesCount;
        }
    }
}
