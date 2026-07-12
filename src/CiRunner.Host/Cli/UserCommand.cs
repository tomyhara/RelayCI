using System.Text;
using CiRunner.Core.Auth;
using CiRunner.Core.Data;
using CiRunner.Core.Paths;

namespace CiRunner.Host.Cli;

/// <summary>
/// `ci-runner.exe user add|passwd|list|remove` (spec §9 "ブートストラップ(初期ユーザー)"): manages
/// `local_users` directly against the DB file, for use while the runner process itself is stopped.
/// Passwords are always read from stdin, never from argv (spec: "コマンドライン引数では渡さない:
/// プロセス一覧・履歴に残るため").
/// </summary>
public static class UserCommand
{
    public static int Run(string[] args, RunnerPaths paths)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: ci-runner.exe user <add|passwd|list|remove> ...");
            return 1;
        }

        var db = new CiDatabase(paths.DbPath);
        db.Migrate();
        var users = new LocalUserRepository(db);
        var settings = new SettingsRepository(db);

        return args[1].ToLowerInvariant() switch
        {
            "add" => Add(args, users, settings),
            "passwd" => Passwd(args, users, settings),
            "list" => List(users),
            "remove" => Remove(args, users),
            _ => Unknown(args[1]),
        };
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"Unknown 'user' subcommand '{sub}'. Expected add, passwd, list, or remove.");
        return 1;
    }

    private static int Add(string[] args, LocalUserRepository users, SettingsRepository settings)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: ci-runner.exe user add <username> [--display-name <name>]");
            return 1;
        }
        var username = args[2];
        string? displayName = null;
        for (var i = 3; i < args.Length; i++)
        {
            if (args[i] == "--display-name" && i + 1 < args.Length)
            {
                displayName = args[++i];
            }
        }

        if (users.FindByUsername(username) is not null)
        {
            Console.Error.WriteLine($"local user '{username}' already exists.");
            return 1;
        }

        var password = ReadPassword($"Password for '{username}': ");
        var minLength = settings.GetInt("minPasswordLength", 8);
        if (password.Length < minLength)
        {
            Console.Error.WriteLine($"Password must be at least {minLength} characters.");
            return 1;
        }

        users.Add(username, Pbkdf2PasswordHasher.Hash(password), displayName);
        Console.WriteLine($"Created local user '{username}'.");
        return 0;
    }

    private static int Passwd(string[] args, LocalUserRepository users, SettingsRepository settings)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: ci-runner.exe user passwd <username>");
            return 1;
        }
        var username = args[2];
        if (users.FindByUsername(username) is null)
        {
            Console.Error.WriteLine($"local user '{username}' not found.");
            return 1;
        }

        var password = ReadPassword($"New password for '{username}': ");
        var minLength = settings.GetInt("minPasswordLength", 8);
        if (password.Length < minLength)
        {
            Console.Error.WriteLine($"Password must be at least {minLength} characters.");
            return 1;
        }

        users.UpdatePassword(username, Pbkdf2PasswordHasher.Hash(password));
        Console.WriteLine($"Password updated for '{username}'.");
        return 0;
    }

    private static int List(LocalUserRepository users)
    {
        var all = users.ListAll();
        if (all.Count == 0)
        {
            Console.WriteLine("(no local users)");
            return 0;
        }
        foreach (var u in all)
        {
            Console.WriteLine($"{u.Username}\t{(u.Enabled ? "enabled" : "disabled")}\t{u.DisplayName}\t{u.CreatedAt}");
        }
        return 0;
    }

    private static int Remove(string[] args, LocalUserRepository users)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: ci-runner.exe user remove <username>");
            return 1;
        }
        var username = args[2];
        if (!users.Delete(username))
        {
            Console.Error.WriteLine($"local user '{username}' not found.");
            return 1;
        }
        Console.WriteLine($"Removed local user '{username}'.");
        return 0;
    }

    /// <summary>Masks input on a real terminal; reads a single line when stdin is redirected (piped
    /// input from a script or an automated test - there is no terminal to mask against there).</summary>
    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine() ?? "";
        }

        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }
                continue;
            }
            if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
            }
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
