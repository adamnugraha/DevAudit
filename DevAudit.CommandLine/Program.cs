﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using CL = CommandLine; //Avoid type name conflict with external CommandLine library

using DevAudit.AuditLibrary;

namespace DevAudit.CommandLine
{
    class Program
    {
        public enum ExitCodes
        {
            SUCCESS = 0,
            INVALID_ARGUMENTS = 1,
            NO_PACKAGE_MANAGER,
            ERROR_SCANNING_FOR_PACKAGES,
            ERROR_SCANNING_SERVER_VERSION,
            ERROR_SEARCHING_OSS_INDEX,

        }

        static Options ProgramOptions = new Options();

        static PackageSource Source { get; set; }

        static ApplicationServer Server { get; set; }

        static Spinner Spinner { get; set; } =  null;

        static int Main(string[] args)
        {
            #region Handle command line options
            Dictionary<string, object> audit_options = new Dictionary<string, object>();
            
            if (!CL.Parser.Default.ParseArguments(args, ProgramOptions))
            {
                return (int)ExitCodes.INVALID_ARGUMENTS;
            }
            else
            {
                if (!string.IsNullOrEmpty(ProgramOptions.File))
                {
                    if (!File.Exists(ProgramOptions.File))
                    {
                        PrintErrorMessage("Error in parameter: Could not find file {0}.", ProgramOptions.File);
                        return (int)ExitCodes.INVALID_ARGUMENTS;
                    }
                    else
                    {
                        audit_options.Add("File", ProgramOptions.File);
                    }
                }
                if (ProgramOptions.Cache)
                {
                   audit_options.Add("Cache", true);
                }

                if (!string.IsNullOrEmpty(ProgramOptions.CacheTTL))
                {
                    audit_options.Add("CacheTTL", ProgramOptions.CacheTTL);
                }

                if (!string.IsNullOrEmpty(ProgramOptions.RootDirectory))
                {
                    audit_options.Add("RootDirectory", ProgramOptions.RootDirectory);
                }

                if (!string.IsNullOrEmpty(ProgramOptions.DockerContainerId))
                {
                    audit_options.Add("DockerContainerId", ProgramOptions.DockerContainerId);
                }

                if (!string.IsNullOrEmpty(ProgramOptions.ConfigurationFile))
                {
                    audit_options.Add("ConfigurationFile", ProgramOptions.ConfigurationFile);
                }

            }
            #endregion

            #region Handle command line verbs
            CL.Parser.Default.ParseArguments(args, ProgramOptions, (verb, options) =>
            {
                try
                {
                    if (verb == "nuget")
                    {
                        Source = new NuGetPackageSource(audit_options);
                    }
                    else if (verb == "msi")
                    {
                        Source = new MSIPackageSource();
                    }
                    else if (verb == "choco")
                    {
                        Source = new ChocolateyPackageSource();
                    }
                    else if (verb == "bower")
                    {
                        Source = new BowerPackageSource(audit_options);
                    }
                    else if (verb == "oneget")
                    {
                        Source = new OneGetPackageSource();
                    }
                    else if (verb == "composer")
                    {
                        Source = new ComposerPackageSource(audit_options);
                    }
                    else if (verb == "dpkg")
                    {
                        Source = new DpkgPackageSource(audit_options);
                    }
                    else if (verb == "drupal8")
                    {
                        Source = new Drupal8Application(audit_options);
                    }
                    else if (verb == "drupal7")
                    {
                        Source = new Drupal7Application(audit_options);
                    }
                    else if (verb == "mysql")
                    {
                        Server = new MySQLServer(audit_options);
                    }
                }
                catch (ArgumentException ae)
                {
                    PrintErrorMessage(ae);
                    return;
                }
                catch (Exception e)
                {
                    PrintErrorMessage(e);
                    return;
                }
            });

            if (Source == null && Server == null)
            {
                Console.WriteLine("No package source or application server specified or error parsing options.");
                return (int)ExitCodes.INVALID_ARGUMENTS;
            }
            #endregion

            if (Server != null) //Auditing an application server
            {
                ExitCodes exit;
                AuditServer(out exit);
                if (Server != null)
                {
                    Server.Dispose();
                }
                return (int) exit;
            }
            else
            {
                PrintMessage("Scanning {0} packages...", Source.PackageManagerLabel);
            }
            StartSpinner();
            try
            {
                Source.PackagesTask.Wait();
            }
            catch (AggregateException ae)
            {
                StopSpinner();
                PrintErrorMessage("Error(s) encountered scanning for {0} packages: {1}", Source.PackageManagerLabel, ae.InnerException.Message);
                return (int)ExitCodes.ERROR_SCANNING_FOR_PACKAGES;
            }
            finally
            {
                StopSpinner();
            }
            PrintMessageLine("Found {0} distinct packages.", Source.Packages.Count());             
            if (ProgramOptions.ListPackages)
            {
                int i = 1;
                foreach (OSSIndexQueryObject package in Source.Packages)
                {
                    PrintMessageLine("[{0}/{1}] {2} {3} {4}", i++, Source.Packages.Count(), package.Name,
                        package.Version, package.Vendor);
                }
                return (int) ExitCodes.SUCCESS;
            }
            if (Source.Packages.Count() == 0)
            {
                PrintMessageLine("Nothing to do, exiting.");
                return (int) ExitCodes.SUCCESS;
            }
            else
            {
                PrintMessage("Searching OSS Index for {0} {1} packages...", Source.Packages.Count(), Source.PackageManagerLabel);
            }
            StartSpinner();
            try
            {
                Task.WaitAll(Source.ArtifactsTask.ToArray());
            }
            catch (AggregateException ae)
            {
                StopSpinner();
                PrintErrorMessage("Error encountered searching OSS Index for {0} packages: {1}...", Source.PackageManagerLabel, ae.InnerException.Message);
                ae.InnerExceptions.ToList().ForEach(i => HandleOSSIndexHttpException(i));
                return (int)ExitCodes.ERROR_SEARCHING_OSS_INDEX;
            }
            finally
            {
                StopSpinner();
            }
            PrintMessageLine("Found {0} artifacts, {1} with an OSS Index project id.", Source.Artifacts.Count(), Source.ArtifactProjects.Count);
            if (Source.Artifacts.Count() == 0)
            {
                PrintMessageLine("Nothing to do, exiting.");
                return (int) ExitCodes.SUCCESS;
            }
            if (ProgramOptions.ListArtifacts)
            {
                int i = 1;
                foreach (OSSIndexArtifact artifact in Source.Artifacts)
                {
                    PrintMessage("[{0}/{1}] {2} ({3}) ", i++, Source.Artifacts.Count(), artifact.PackageName,
                        !string.IsNullOrEmpty(artifact.Version) ? artifact.Version : string.Format("No version reported for package version {0}", artifact.Package.Version));
                    if (!string.IsNullOrEmpty(artifact.ProjectId))
                    {
                        PrintMessage(ConsoleColor.Blue, artifact.ProjectId + "\n");
                    }
                    else
                    {
                        PrintMessage(ConsoleColor.DarkRed, "No project id found.\n");
                    }
                }
                return 0;
            }
            if (ProgramOptions.CacheDump)
            {
                Console.WriteLine("Dumping cache...");
                Console.WriteLine("\nCurrently Cached Items\n===========");
                int i = 0;
                foreach (var v in Source.ProjectVulnerabilitiesCacheItems)
                {
                    Console.WriteLine("{0} {1} {2}", i++, v.Item1.Id, v.Item1.Name);

                }
                int k = 0;
                Console.WriteLine("\nCache Keys\n==========");
                foreach (var v in Source.ProjectVulnerabilitiesCacheKeys)
                {
                    Console.WriteLine("{0} {1}", k++, v);

                }
                Source.Dispose();
                return 0;
            }
            if (Source.ArtifactProjects.Count == 0)
            {
                PrintMessageLine("No found artifacts have associated projects.");
                PrintMessageLine("No vulnerability data for your packages currently exists in OSS Index, exiting.");
                return (int) ExitCodes.SUCCESS;
            }
            if (ProgramOptions.Cache)
            {                
                PrintMessageLine("{0} projects have cached values.", Source.CachedArtifacts.Count());
                PrintMessageLine("{0} cached project entries are stale and will be removed from cache.", Source.ProjectVulnerabilitiesExpiredCacheKeys.Count());
            }
            PrintMessageLine("Searching OSS Index for vulnerabilities for {0} projects...", Source.VulnerabilitiesTask.Count());
            int projects_count = Source.ArtifactProjects.Count;
            int projects_processed = 0;
            int projects_successful = 0;
            if (Source.ProjectVulnerabilitiesCacheEnabled)
            {                
                foreach (Tuple<OSSIndexProject, IEnumerable<OSSIndexProjectVulnerability>> c in Source.ProjectVulnerabilitiesCacheItems)
                {
                    if (projects_processed++ ==0 ) Console.WriteLine("\nAudit Results\n=============");                    
                    OSSIndexProject p = c.Item1;
                    IEnumerable<OSSIndexProjectVulnerability> vulnerabilities = c.Item2;
                    OSSIndexArtifact a = p.Artifact;
                    PrintMessage(ConsoleColor.White, "[{0}/{1}] {2}", projects_processed, projects_count, a.PackageName);
                    PrintMessage(ConsoleColor.DarkCyan, "[CACHED]");
                    PrintMessage(ConsoleColor.White, " {0} ", a.Version);
                    if (vulnerabilities.Count() == 0)
                    {
                        PrintMessageLine(ConsoleColor.DarkGray, "No known vulnerabilities.");
                    }
                    else
                    {
                        List<OSSIndexProjectVulnerability> found_vulnerabilities = new List<OSSIndexProjectVulnerability>(vulnerabilities.Count());
                        foreach (OSSIndexProjectVulnerability vulnerability in vulnerabilities.GroupBy(v => new { v.CVEId, v.Uri, v.Title, v.Summary }).SelectMany(v => v).ToList())
                        {
                            try
                            {
                                if (vulnerability.Versions.Any(v => !string.IsNullOrEmpty(v) && Source.IsVulnerabilityVersionInPackageVersionRange(v, a.Package.Version)))
                                {
                                    found_vulnerabilities.Add(vulnerability);
                                }
                            }
                            catch (Exception e)
                            {
                                PrintErrorMessage("Error determining vulnerability version range {0} in package version range {1}: {2}.",
                                    vulnerability.Versions.Aggregate((f, s) => { return f + "," + s; }), a.Package.Version, e.Message);
                            }
                        }
                        //found_vulnerabilities = found_vulnerabilities.GroupBy(v => new { v.CVEId, v.Uri, v.Title, v.Summary }).SelectMany(v => v).ToList();
                        if (found_vulnerabilities.Count() > 0)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine("[VULNERABLE]");
                        }
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write("{0} distinct ({1} total) known vulnerabilities, ", vulnerabilities.GroupBy(v => new { v.CVEId, v.Uri, v.Title, v.Summary }).SelectMany(v => v).Count(),
                            vulnerabilities.Count());
                        Console.WriteLine("{0} affecting installed version.", found_vulnerabilities.Count());
                        found_vulnerabilities.ForEach(v =>
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            if (!string.IsNullOrEmpty(v.CVEId)) Console.Write("{0} ", v.CVEId);
                            Console.WriteLine(v.Title);
                            Console.ResetColor();
                            Console.WriteLine(v.Summary);
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.Write("\nAffected versions: ");
                            Console.ForegroundColor = ConsoleColor.White;
                            Console.WriteLine(string.Join(", ", v.Versions.ToArray()));
                        });
                    }
                    Console.ResetColor();
                    projects_successful++;
                }
            }    
            while (Source.VulnerabilitiesTask.Count() > 0)
            {
                Task<KeyValuePair<OSSIndexProject, IEnumerable<OSSIndexProjectVulnerability>>>[] tasks = Source.VulnerabilitiesTask.ToArray();                                
                try
                {
                    int x = Task.WaitAny(tasks);
                    var task = Source.VulnerabilitiesTask.Find(t => t.Id == tasks[x].Id);
                    KeyValuePair<OSSIndexProject, IEnumerable<OSSIndexProjectVulnerability>> vulnerabilities = task.Result;
                    OSSIndexProject p = vulnerabilities.Key;
                    OSSIndexArtifact a = p.Artifact;
                    KeyValuePair<OSSIndexQueryObject, IEnumerable<OSSIndexPackageVulnerability>> package_vulnerabilities
                        = Source.PackageVulnerabilities.Where(pv => pv.Key == p.Package).First();
                    if (projects_processed++ == 0)
                    {
                        PrintMessageLine("\nAudit Results\n=============");
                    }
                    projects_successful++;
                    PrintMessage(ConsoleColor.White, "[{0}/{1}] {2} {3}", projects_processed, projects_count, a.PackageName, string.IsNullOrEmpty(a.Version) ? "" : 
                        string.Format("({0}) ", a.Version));
                    if (package_vulnerabilities.Value.Count() == 0 && vulnerabilities.Value.Count() == 0)
                    {
                        PrintMessage(ConsoleColor.DarkGray, "no known vulnerabilities. ");
                        PrintMessage("[{0} {1}]\n", p.Package.Name, p.Package.Version);
                    }
                    else
                    {
                      
                     List<OSSIndexPackageVulnerability> found_package_vulnerabilities = new List<OSSIndexPackageVulnerability>();
                        foreach (OSSIndexPackageVulnerability package_vulnerability in package_vulnerabilities.Value)
                        {

                            try
                            {
                                if (package_vulnerability.Versions.Any(v => !string.IsNullOrEmpty(v) && Source.IsVulnerabilityVersionInPackageVersionRange(v, p.Package.Version)))
                                {
                                    found_package_vulnerabilities.Add(package_vulnerability);
                                }
                            }
                            catch (Exception e)
                            {
                                PrintErrorMessage("Error determining vulnerability version range {0} in package version range {1}: {2}",
                                    package_vulnerability.Versions.Aggregate((f, s) => { return f + "," + s; }), a.Package.Version, e.Message);
                            }
                        }
                        List<OSSIndexProjectVulnerability> found_vulnerabilities = new List<OSSIndexProjectVulnerability>(vulnerabilities.Value.Count());
                        foreach (OSSIndexProjectVulnerability vulnerability in vulnerabilities.Value.GroupBy(v => new { v.CVEId, v.Uri, v.Title, v.Summary }).SelectMany(v => v).ToList())
                        {
                            try
                            {
                                if (vulnerability.Versions.Any(v => !string.IsNullOrEmpty(v) && Source.IsVulnerabilityVersionInPackageVersionRange(v, p.Package.Version)))
                                {
                                    found_vulnerabilities.Add(vulnerability);
                                }
                            }
                            catch (Exception e)
                            {
                                PrintErrorMessage("Error determining vulnerability version range ({0}) in project version range ({1}). Message: {2}",
                                    vulnerability.Versions.Aggregate((f, s) => { return f + "," + s; }), a.Package.Version, e.Message);
                            }
                        }
                        //found_vulnerabilities = found_vulnerabilities.GroupBy(v => new { v.CVEId, v.Uri, v.Title, v.Summary }).SelectMany(v => v).ToList();
                        if (found_vulnerabilities.Count() > 0 || found_package_vulnerabilities.Count() > 0)
                        {
                            PrintMessageLine(ConsoleColor.Red, "[VULNERABLE]");
                        }
                        PrintMessage(ConsoleColor.Magenta, "{0} known vulnerabilities, ", vulnerabilities.Value.Count() + package_vulnerabilities.Value.Count()); //vulnerabilities.Value.GroupBy(v => new { v.CVEId, v.Uri, v.Title, v.Summary }).SelectMany(v => v).Count(),
                        PrintMessage("{0} affecting installed version. ", found_vulnerabilities.Count() + found_package_vulnerabilities.Count());
                        PrintMessageLine("[{0} {1}]", p.Package.Name, p.Package.Version);
                        found_package_vulnerabilities.ForEach(v =>
                        {
                            PrintMessageLine(ConsoleColor.Red, "{0} {1}", v.Id.Trim(), v.Title.Trim());
                            PrintMessageLine(v.Summary);
                            PrintMessage(ConsoleColor.Red, "Affected versions: ");
                            
                            PrintMessageLine(ConsoleColor.White, string.Join(", ", v.Versions.ToArray()));
                            PrintMessageLine("");
                        });
                        found_vulnerabilities.ForEach(v =>
                        {
                            PrintMessageLine(ConsoleColor.Red, "{0} {1}", v.CVEId, v.Title);
                            PrintMessageLine(v.Summary);
                            PrintMessage(ConsoleColor.Red, "Affected versions: ");
                            PrintMessageLine(ConsoleColor.White, string.Join(", ", v.Versions.ToArray()));
                            PrintMessageLine("");
                        });
                    }                    
                    Source.VulnerabilitiesTask.Remove(task);
                    projects_successful++;
                }
                catch (AggregateException ae)
                {
                    if (projects_processed++ == 0)
                    {
                        PrintMessageLine("\nAudit Results\n=============");
                    }
                    var failed_tasks = Source.VulnerabilitiesTask.Where(t => t.Status == TaskStatus.Faulted || t.Status == TaskStatus.Canceled).ToList();
                    foreach (var t in failed_tasks)
                    {
                        if (t.Exception != null && t.Exception.InnerException is OSSIndexHttpException)
                        {
                            OSSIndexHttpException oe = t.Exception.InnerException as OSSIndexHttpException;
                            OSSIndexArtifact artifact = Source.Artifacts.FirstOrDefault(a => a.ProjectId == oe.RequestParameter || a.PackageId == oe.RequestParameter);
                            Console.Write("[{0}/{1}] {2} ", ++projects_processed, projects_count, artifact.PackageName, artifact.Version);
                            PrintMessageLine(ConsoleColor.DarkRed, "{0} HTTP Error searching OSS Index...", artifact.Version);
                            ++projects_processed;
                            HandleOSSIndexHttpException(oe);
                            //ae.InnerExceptions.ToList().ForEach(i => HandleOSSIndexHttpException(i));
                        }
                        else
                        {
                            PrintErrorMessage("Unknown error encountered searching OSS Index for vulnerabilities : {0}",
                                ae.InnerException.Message);
                            ++projects_processed;
                        }
                        Source.VulnerabilitiesTask.Remove(t);
                    }
                    //projects_processed += Source.VulnerabilitiesTask.Count(t => t.Status == TaskStatus.Faulted || t.Status == TaskStatus.Canceled) - 1;
                   
                }
            }
            Source.Dispose();
            return 0;
        }

        #region Static methods
        static void AuditServer(out ExitCodes exit)
        {
            if (ReferenceEquals(Server, null)) throw new ArgumentNullException("Server");
            PrintMessage("Scanning {0} modules, extensions, or plugins.", Server.ServerLabel);
            StartSpinner();
            exit = ExitCodes.ERROR_SCANNING_SERVER_VERSION;
            string version;
            try
            {
                version = Server.GetVersion().Trim();
                Server.ModulesTask.Wait();
                Server.PackagesTask.Wait();
            }
            catch (Exception e)
            {
                PrintErrorMessage(e);
                return;
            }
            finally
            {
                StopSpinner();
            }
            PrintMessageLine("Detected {0} version: {1}.", Server.ServerLabel, version);
            exit = ExitCodes.ERROR_SCANNING_FOR_PACKAGES;
            if (ProgramOptions.ListPackages)
            {
                int i = 1;
                foreach (OSSIndexQueryObject package in Server.Packages)
                {
                    PrintMessageLine("[{0}/{1}] {2} {3} {4}", i++, Server.Packages.Count(), package.Name,
                        package.Version, package.Vendor);
                }
                exit = ExitCodes.SUCCESS;
                return;
            }
            if (Server.Packages.Count() == 0)
            {
                exit = ExitCodes.SUCCESS;
                return;
            }
            else
            {
                PrintMessage("Searching OSS Index for {0} {1} packages...", Server.Packages.Count(), Server.ServerLabel);

            }
            exit = ExitCodes.SUCCESS;
            return;

        }

        static void PrintMessage(string format)
        {
            Console.Write(format);
        }

        static void PrintMessage(string format, params object[] args)
        {
            Console.Write(format, args);
        }

        static void PrintMessage(ConsoleColor color, string format)
        {
            if (!ProgramOptions.NonInteractive)
            {
                ConsoleColor o = Console.ForegroundColor;
                Console.ForegroundColor = color;
                PrintMessage(format);
                Console.ForegroundColor = o;
            }
            else
            {
                Console.Write(format);
            }
        }

        static void PrintMessage(ConsoleColor color, string format, params object[] args)
        {
            if (!ProgramOptions.NonInteractive)
            {
                ConsoleColor o = Console.ForegroundColor;
                Console.ForegroundColor = color;
                PrintMessage(format, args);
                Console.ForegroundColor = o;
            }
            else
            {
                Console.Write(format, args);
            }
        }

        static void PrintMessageLine(string format)
        {
            Console.WriteLine(format);
        }

        static void PrintMessageLine(string format, params object[] args)
        {
            Console.WriteLine(format, args);
        }

        static void PrintMessageLine(ConsoleColor color, string format, params object[] args)
        {
            if (!ProgramOptions.NonInteractive)
            {
                ConsoleColor o = Console.ForegroundColor;
                Console.ForegroundColor = color;
                PrintMessageLine(format, args);
                Console.ForegroundColor = o;
            }
            else
            {
               PrintMessageLine(format, args);
            }
        }

        static void PrintErrorMessage(string format, params object[] args)
        {
            PrintMessageLine(ConsoleColor.DarkRed, format, args);
        }

        static void PrintErrorMessage(Exception e)
        {
            PrintMessageLine(ConsoleColor.DarkRed, "Exception: {0}", e.Message);
            PrintMessageLine(ConsoleColor.DarkRed, "Stack trace: {0}", e.StackTrace);

            if (e.InnerException != null)
            {
                PrintMessageLine(ConsoleColor.DarkRed, "Inner exception: {0}", e.InnerException.Message);
            }
        }

        static void HandleOSSIndexHttpException(Exception e)
        {
            if (e.GetType() == typeof(OSSIndexHttpException))
            {
                OSSIndexHttpException oe = (OSSIndexHttpException) e;
                PrintErrorMessage("HTTP status: {0} {1} \nReason: {2}\nRequest:\n{3}", (int) oe.StatusCode, oe.StatusCode, oe.ReasonPhrase, oe.Request);
            }

        }

        static void StartSpinner()
        {
            if (!ProgramOptions.NonInteractive)
            {
                Spinner = new Spinner(50);
                Spinner.Start();
            }
        }

        static void StopSpinner()
        {
            if (ReferenceEquals(null, Spinner)) throw new ArgumentNullException();
            if (!ProgramOptions.NonInteractive)
            {
                Spinner.Stop();
                Spinner = null;
                PrintMessageLine(string.Empty);
            }

        }
        #endregion

    }
}
