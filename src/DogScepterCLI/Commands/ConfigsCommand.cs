﻿using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using DogScepterLib;
using System.IO;
using System.Threading.Tasks;

namespace DogScepterCLI.Commands;

/// <summary>
/// The "configs" command, which lists available configuration files.
/// </summary>
[Command("configs", Description = "Lists available configuration files.")]
// ReSharper disable once UnusedType.Global - used as a Command for CliFix
public class ConfigsCommand : ICommand
{
    public ValueTask ExecuteAsync(IConsole console)
    {
        console.Output.WriteLine();

        console.Output.WriteLine("Macro type config files:");
        foreach (string f in GameConfigs.FindAllMacroTypes())
            console.Output.WriteLine(Path.GetFileNameWithoutExtension(f));

        return default;
    }
}