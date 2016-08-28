using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Agent.Worker.Handlers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    [ServiceLocator(Default = typeof(TaskRunner))]
    public interface ITaskRunner : IStep, IAgentService
    {
        TaskInstance TaskInstance { get; set; }
    }

    public sealed class TaskRunner : AgentService, ITaskRunner
    {
        public bool AlwaysRun => TaskInstance?.AlwaysRun ?? default(bool);
        public bool ContinueOnError => TaskInstance?.ContinueOnError ?? default(bool);
        public bool Critical => false;
        public string DisplayName => TaskInstance?.DisplayName;
        public bool Enabled => TaskInstance?.Enabled ?? default(bool);
        public IExecutionContext ExecutionContext { get; set; }
        public bool Finally => false;
        public TaskInstance TaskInstance { get; set; }

        public async Task RunAsync()
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(ExecutionContext, nameof(ExecutionContext));
            ArgUtil.NotNull(ExecutionContext.Variables, nameof(ExecutionContext.Variables));
            ArgUtil.NotNull(TaskInstance, nameof(TaskInstance));
            var taskManager = HostContext.GetService<ITaskManager>();
            var handlerFactory = HostContext.GetService<IHandlerFactory>();

            // Set the task display name variable.
            ExecutionContext.Variables.Set(Constants.Variables.Task.DisplayName, DisplayName);

            // Load the task definition and choose the handler.
            // TODO: Add a try catch here to give a better error message.
            Definition definition = taskManager.Load(TaskInstance);
            ArgUtil.NotNull(definition, nameof(definition));

            foreach (var data in definition.Data.Execution.All)
            {
                foreach (var c in data.Conditions)
                {
                    Trace.Info($"{c.Key} = {c.Value}");
                }
            }

            HandlerData handlerData;
            if (definition.Data.Execution.SupportCondition)
            {
                List<HandlerData> matchedHandlers = new List<HandlerData>();
                var extensionManager = HostContext.GetService<IExtensionManager>();
                var evaluators = extensionManager.GetExtensions<IHandlerConditionEvaluator>();
                foreach (var execution in definition.Data.Execution.All)
                {
                    Trace.Info(execution.GetType());
                    if (execution.Conditions.Count == 0)
                    {
                        Trace.Info($"Skip {execution.GetType().Name} which has empty conditions.");
                        continue;
                    }

                    bool candidate = true;
                    foreach (var condition in execution.Conditions)
                    {
                        Trace.Info($"Testing condition: {condition.Key} = {condition.Value.ToString()}");
                        var evaluator = evaluators.Where(e => e.Name.Equals(condition.Key, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
                        Trace.Info(evaluator.GetType());
                        if (!evaluator.IsConditionMatch(condition.Value))
                        {
                            candidate = false;
                            break;
                        }
                    }

                    if (candidate)
                    {
                        Trace.Info($"Add: {execution.GetType()}");
                        matchedHandlers.Add(execution);
                    }
                }

                if (matchedHandlers.Count == 0)
                {
                    throw new ArgumentException("Can't find matched handler.");
                }

                handlerData = matchedHandlers
                              .OrderBy(x => x.Priority)
                              .FirstOrDefault();
            }
            else
            {
                handlerData = definition.Data?.Execution?.All
                              .OrderBy(x => !x.PreferredOnCurrentPlatform()) // Sort true to false.
                              .ThenBy(x => x.Priority)
                              .FirstOrDefault();
            }

            if (handlerData == null)
            {
                string[] supportedHandlers;
#if OS_WINDOWS
                supportedHandlers = new string[] { "Node", "PowerShell3", "PowerShell", "AzurePowerShell", "PowerShellExe", "Process" };
#else
                supportedHandlers = new string[] { "Node" };
#endif                
                throw new Exception(StringUtil.Loc("SupportedTaskHandlerNotFound", string.Join(", ", supportedHandlers)));
            }

            // Load the default input values from the definition.
            Trace.Verbose("Loading default inputs.");
            var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var input in (definition.Data?.Inputs ?? new TaskInputDefinition[0]))
            {
                string key = input?.Name?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(key))
                {
                    inputs[key] = input.DefaultValue?.Trim() ?? string.Empty;
                }
            }

            // Merge the instance inputs.
            Trace.Verbose("Loading instance inputs.");
            foreach (var input in (TaskInstance.Inputs as IEnumerable<KeyValuePair<string, string>> ?? new KeyValuePair<string, string>[0]))
            {
                string key = input.Key?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(key))
                {
                    inputs[key] = input.Value?.Trim() ?? string.Empty;
                }
            }

            // Expand the inputs.
            Trace.Verbose("Expanding inputs.");
            ExecutionContext.Variables.ExpandValues(target: inputs);
            VarUtil.ExpandEnvironmentVariables(HostContext, target: inputs);

            // Translate the server file path inputs to local paths.
            foreach (var input in definition.Data?.Inputs ?? new TaskInputDefinition[0])
            {
                if (string.Equals(input.InputType, TaskInputType.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    Trace.Verbose($"Translating file path input '{input.Name}': '{inputs[input.Name]}'");
                    inputs[input.Name] = TranslateFilePathInput(inputs[input.Name] ?? string.Empty);
                    Trace.Verbose($"Translated file path input '{input.Name}': '{inputs[input.Name]}'");
                }
            }

            // Expand the handler inputs.
            Trace.Verbose("Expanding handler inputs.");
            VarUtil.ExpandValues(HostContext, source: inputs, target: handlerData.Inputs);
            ExecutionContext.Variables.ExpandValues(target: handlerData.Inputs);

            // Create the handler.
            IHandler handler = handlerFactory.Create(
                ExecutionContext,
                handlerData,
                inputs,
                taskDirectory: definition.Directory,
                filePathInputRootDirectory: TranslateFilePathInput(string.Empty));

            // Run the task.
            await handler.RunAsync();
        }

        private string TranslateFilePathInput(string inputValue)
        {
            Trace.Entering();

#if OS_WINDOWS
            Trace.Verbose("Trim double quotes around filepath type input on Windows.");
            inputValue = inputValue.Trim('\"');
#endif 
            // if inputValue is rooted, return full path.
            string fullPath;
            if (!string.IsNullOrEmpty(inputValue) &&
                inputValue.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
                Path.IsPathRooted(inputValue))
            {
                try
                {
                    fullPath = Path.GetFullPath(inputValue);
                    Trace.Info($"The original input is a rooted path, return absolute path: {fullPath}");
                    return fullPath;
                }
                catch (Exception ex)
                {
                    Trace.Error(ex);
                    Trace.Info($"The original input is a rooted path, but it is not full qualified, return the path: {inputValue}");
                    return inputValue;
                }
            }

            // use jobextension solve inputValue, if solved result is rooted, return full path.
            var extensionManager = HostContext.GetService<IExtensionManager>();
            IJobExtension[] extensions =
                (extensionManager.GetExtensions<IJobExtension>() ?? new List<IJobExtension>())
                .Where(x => string.Equals(x.HostType, ExecutionContext.Variables.System_HostType, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (IJobExtension extension in extensions)
            {
                fullPath = extension.GetRootedPath(ExecutionContext, inputValue);
                if (!string.IsNullOrEmpty(fullPath))
                {
                    // Stop on the first path root found.
                    Trace.Info($"{extension.HostType} JobExtension resolved a rooted path:: {fullPath}");
                    return fullPath;
                }
            }

            // return original inputValue.
            Trace.Info("Can't root path even by using JobExtension, return original input.");
            return inputValue;
        }
    }
}
