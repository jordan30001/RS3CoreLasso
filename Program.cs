using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Configuration;

class Program
{
    // ================= CONFIG =================
    static int CoresPerGameProcess;
    static int CoresPerShaderProcess;
    static int[] GameCoreList;
    static int[] ShaderCoreList;

    // ================= STATE =================
    static int[] CoreUsage;
    readonly static object Lock = new();

    static void Main()
    {
        // Load configuration from appsettings.json
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        // Read configuration values
        CoresPerGameProcess = configuration.GetValue<int>("AppSettings:CoresPerGameProcess", 2);
        CoresPerShaderProcess = configuration.GetValue<int>("AppSettings:CoresPerShaderProcess", 1);
        var tempGameCoreList = configuration.GetSection("AppSettings:GameCoreList").Get<int[]>();
        var tempShaderCoreList = configuration.GetSection("AppSettings:ShaderCoreList").Get<int[]>();
        int totalCores = Environment.ProcessorCount;
        CoreUsage = new int[totalCores];
        if (tempGameCoreList is null)
        {
            GameCoreList = Enumerable.Range(1, totalCores).ToArray();
        }
        else
        {
            GameCoreList = tempGameCoreList;
        }
        if (tempShaderCoreList is null)
        {
            ShaderCoreList = Enumerable.Range(1, totalCores).ToArray();
        }
        else
        {
            ShaderCoreList = tempShaderCoreList;
        }

        Console.WriteLine($"Configuration loaded: {CoresPerGameProcess} cores per game process, {CoresPerShaderProcess} cores per shader process");
        Console.WriteLine($"Game cores (1-indexed): [{string.Join(", ", GameCoreList)}]");
        Console.WriteLine($"Shader cores (1-indexed): [{string.Join(", ", ShaderCoreList)}]");

        // Start background thread to update core usage every minute
        var usageThread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    lock (Lock)
                    {
                        CoreUsage = GetCoreUsage();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error updating core usage: {ex.Message}");
                }

                Thread.Sleep(TimeSpan.FromMinutes(1));
            }
        })
        {
            IsBackground = true
        };
        usageThread.Start();
        Console.WriteLine("Core usage monitoring thread started (updates every 60 seconds)");

        using (var session = new TraceEventSession("Rs2ClientWatcher"))
        {
            session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.Process
            );

            session.Source.Kernel.ProcessStart += OnProcessStart;
            Console.WriteLine("Starting ETW session...");
            session.Source.Process();
            Console.WriteLine("ETW session ended.");
        }
    }

    static void OnProcessStart(ProcessTraceData data)
    {
        // ignore any applications that aren't the game client
        if (data.ProcessName.Equals("rs2client.exe", StringComparison.OrdinalIgnoreCase) is false)
            return;

        // get the clients command line arguments
        string cmd = data.CommandLine ?? "";

        // check if the game client has the compileshader flag, if it does, then it's a subprocess, and not the actual game client.
        bool isShader = cmd.Contains("--compileshader");

        //device which cores to use based on if it's a shader process or a game process
        var pool = isShader ? ShaderCoreList : GameCoreList;
        int[] selected;
        lock (Lock)
        {
            selected = isShader ? PickLeastUsedCores(pool, CoreUsage, CoresPerShaderProcess) : PickLeastUsedCores(pool, CoreUsage, CoresPerGameProcess);

            if (selected is null || selected.Length == 0)
            {
                Console.WriteLine($"No available cores for PID {data.ProcessID} isShader={isShader}, pool=[{string.Join(",", pool)}]");
                return;
            }

            foreach (var core in selected)
            {
                CoreUsage[core - 1]++;
            }
        }


        // convert the selected cores to a bitmask for processor affinity
        long mask = ConvertToAffinityMask(selected);

        try
        {
            var proc = Process.GetProcessById(data.ProcessID);
            proc.ProcessorAffinity = (IntPtr)mask;

            Console.WriteLine($"PID {data.ProcessID} -> cores [{string.Join(",", selected)}] mask={mask}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed PID {data.ProcessID}: {ex.Message}");
        }
    }

    static int[] PickLeastUsedCores(int[] pool, int[] poolUsage, int count)
    {
        // Sort the pool by usage (0-indexed usage array, but pool contains 1-indexed core IDs)
        // Create pairs of (coreId, usage) and sort by usage ascending
        var coresByUsage = pool
            .Select(coreId => new { CoreId = coreId, Usage = poolUsage[coreId - 1] }) // Convert 1-indexed to 0-indexed for usage lookup
            .OrderBy(x => x.Usage)
            .ThenBy(x => x.CoreId) // Secondary sort by core ID for consistency
            .Take(count)
            .Select(x => x.CoreId)
            .ToArray();

        return coresByUsage;
    }

    static int[] GetCoreUsage()
    {
        // Get the total number of logical processor cores on the system
        int totalCores = Environment.ProcessorCount;

        // Find all running processes with the name "rs2client"
        var processes = Process.GetProcessesByName("rs2client");

        // Initialize an array to track how many processes are assigned to each core
        var usage = new int[totalCores];

        // Iterate through each rs2client process
        foreach (var process in processes)
        {
            try
            {
                // Get the processor affinity mask (bitmask indicating which cores this process can run on)
                long affinityMask = (long)process.ProcessorAffinity;

                // Check each core to see if the process is assigned to it
                for (int i = 0; i < totalCores; i++)
                {
                    // Check if the i-th bit is set in the affinity mask
                    // If set, this process is allowed to run on core i
                    if ((affinityMask & (1L << i)) != 0)
                    {
                        // Increment the usage counter for this core
                        usage[i]++;
                    }
                }
            }
            catch (Exception)
            {
                // Ignore errors (process may have exited or access denied)
            }
        }

        return usage;
    }

    static long ConvertToAffinityMask(int[] cores)
    {
        long mask = 0;

        // converts 1-indexed core IDs to a bitmask for processor affinity
        // example, on a 8 core system
        // 1111 1111 = all cores
        // 0000 1010 = cores 2 and 4
        foreach (int core in cores)
            mask |= 1L << (core - 1);

        return mask;
    }

    static int[] GetAffinityCores(long mask)
    {
        var list = new List<int>();

        for (int i = 0; i < 64; i++)
        {
            if ((mask & (1L << i)) != 0)
                list.Add(i + 1);
        }

        return list.ToArray();
    }
}