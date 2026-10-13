# tools/TestDatabase

Provisions the separate test database `nivora_test` on the same Neon server as the development database. Run by hand by the owner. Tests and app startup never provision.

## Setup: the test secret

`dotnet user-secrets set <key> <value>` would put the value on a command line, so do not use it. Instead:

1. Close everything that can write the secrets file.
2. Open `%APPDATA%\Microsoft\UserSecrets\fd1812ca-9444-4a77-b0b9-082ba45bb173\secrets.json` in an editor.
3. Copy the `ConnectionStrings:DefaultConnection` line and rename its key to `ConnectionStrings:TestConnection`.
4. In the copied value change only the path segment after the host from `/neondb` to `/nivora_test`. Host, user, password and query stay the same (same server).
5. Leave `ConnectionStrings:DefaultConnection` unchanged. Save.

Confirm both key names exist by reading them in the editor, not with `dotnet user-secrets list` (it prints values).

## Usage

```
dotnet run --project tools/TestDatabase                      # create nivora_test (stops if it exists)
dotnet run --project tools/TestDatabase -- rebuild           # drop nivora_test WITH (FORCE), then create
dotnet run --project tools/TestDatabase -- --self-test       # no secret, no connection
dotnet run --project tools/TestDatabase -- --write-script <absolute-path>   # no secret, no connection
```

Create and rebuild:

1. The first line printed is `host and database name check: yes` or `... no`. On `no` the tool exits with code 2 and prints nothing else. The check is: the test key's host equals the development key's host, and the test key's database name is exactly `nivora_test`. A missing or unparsable key is a `no`.
2. Generates the migration script in process (no connection), creates the database, creates the `vector` extension (the server's version, unpinned), applies the script through `psql`, creates the three roles through `RoleSeeder`, and prints a report ending in nine catalog digest lines.
3. `rebuild` produces the same schema, the same applied migration list and the same three role names every time. The `vector` version and generated Ids may differ; the report shows the vector version.

Requires `psql.exe` (on `PATH` or at `C:\Program Files\PostgreSQL\18\bin\psql.exe`), used as a client only.

`--write-script <path>`: the path must be absolute, must not exist, its folder must exist, and it must not lie under the repository.

## CI role (`nivora_ci`)

CI (`.github/workflows/ci.yml`) connects as the Neon role `nivora_ci`, which can reach `nivora_test` and no other database. CI never provisions and never migrates. A `rebuild` drops `nivora_test` and, with it, every grant the role has on it. So the order after every rebuild is:

1. `dotnet run --project tools/TestDatabase -- rebuild`
2. The grant script, by hand, as the shared role, connected to `nivora_test` (psql asks for the password; no value goes on the command line):

   ```
   psql -X -v ON_ERROR_STOP=1 -d "host=<host> dbname=nivora_test user=<shared role> sslmode=require" -f tools/TestDatabase/grant-ci-role.sql
   ```

   Its last output is one row: `tables` equals `readable`, and `history_writable` is `f`.
3. CI is green again.

What forgetting step 2 looks like: the CI step "Assert the migration head of nivora_test" fails, and the test's message is `The staleness check could not read nivora_test (PostgresException, SQLSTATE 42501)`. `28000` or `28P01` in that message means the role or its password is wrong, not the grants. A connection failure through the pooler host has also been seen to reach Npgsql as a plain NpgsqlException with no SQLSTATE at all; treat a message with no SQLSTATE the same way — check the connection string, not the grants. A "migration history does not match" message means step 1 is needed first.

The role itself is not dropped by a rebuild. It is created once (docs/handoffs/ci-design.md, C1).

## Rules

- Never run `rebuild` while a test run is in progress, local or CI: `WITH (FORCE)` ends its sessions. To see whether a CI run is in progress: GitHub, Actions tab, workflow `CI`, no run shown as `In progress` or `Queued` (or `gh run list --workflow ci.yml --status in_progress` and the same with `--status queued`, both empty).
- The tool reads the application's own connection key for its host check, so do not run it while that key is parked.
- After adding a migration and applying it to the development database as usual, the next test run fails with a "rebuild" message: run `rebuild`.
- The tool only ever creates or drops a database named `nivora_test`. It never connects with the application's own connection string.
- Exit codes: 0 success, 1 failure (including an argument error), 2 the host and database name check said `no`.
