# Contributing to Pulaski

Thanks for your interest in Pulaski. Contributions are welcome from developers, controls engineers, quality engineers, and anyone who has worked on a plant floor. Domain knowledge is as valuable here as code.

> Pulaski is pre-alpha. Expect the architecture and APIs to change, and expect to discuss your idea in an issue before writing much code.

## Ways to contribute

- **Domain expertise.** Review the domain model, especially lot genealogy for continuous processes, machine state models, and SPC rules. Real-world examples of how your lines work are extremely helpful.
- **Issues.** Report bugs, propose features, or point out where Pulaski doesn't match how plants actually run.
- **Code.** Fix bugs, build features, write tests, or add protocol and ERP connectors.
- **Documentation.** Improve guides, examples, and explanations.

## Before you start

1. **Search existing issues** to see whether your idea or bug is already being discussed.
2. **Open an issue first** for anything bigger than a small fix. Describe the problem and your proposed approach, and wait for feedback before investing significant time.
3. **Check the v1 scope.** Pulaski is deliberately focused on process and semi-continuous manufacturing. It is not an ERP, a SCADA/HMI, or a historian. Proposals outside that focus may be declined or deferred.

## Clean-room rule

Only submit work you have the right to contribute.

- Do **not** submit code, database schemas, stored procedures, configuration, documentation, or data taken from your employer, a client, or any proprietary MES or ERP product.
- Do **not** reproduce material from commercial vendors' documentation or software.
- Public standards (ISA-95, ISA-88, B2MML, OPC UA, Sparkplug B) and your own general knowledge and experience are fine.

If you are unsure whether you can contribute something, ask in an issue before submitting it.

## Development setup

You need the .NET SDK that `global.json` names (10.0.404, or a later 10.0.4xx patch), PostgreSQL 18 (local, or the `postgres:18` container), Docker for the machine simulators, and Git.

- Build: `dotnet build src/Pulaski.slnx`
- Test: `dotnet test src/Pulaski.slnx`. The database tests create and drop a throwaway database on the local server; point them at another
  with `PULASKI_TEST_ADMIN_CONNECTION` (a user that can create databases and roles), for example
  `Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres`. The OPC UA server tests run only when
  `PULASKI_OPCUA_TEST_ENDPOINT` names a server, such as the simulator in `deploy/simulator/compose.yaml` (`opc.tcp://localhost:50000`).
- Loading a database and running the API and UI locally are described in `CLAUDE.md` under *Local dev*.

Every pull request runs the same build and tests on GitHub Actions (`.github/workflows/ci.yml`): the whole solution on Linux against
PostgreSQL 18 and the OPC UA simulator, and the edge node on Windows. A pull request should pass before it is merged.

## Making changes

1. Fork the repository and create a branch from `main`:
   `git checkout -b feature/short-description` or `fix/short-description`
2. Keep each pull request focused on one change.
3. Add or update tests for any behavior you change.
4. Update documentation when behavior or configuration changes.
5. Make sure the build and all tests pass locally; CI runs them again on the pull request.

### Coding guidelines

- Follow standard .NET naming and formatting conventions. The repository's `.editorconfig` is authoritative once added.
- Enable nullable reference types and treat warnings seriously.
- Use ISA-95 terminology for domain concepts (equipment, material lot, segment, work order) so the model stays recognizable to MES practitioners.
- Production events are append-only. Never update or delete an event; record a new one.
- Prefer small, readable code over clever code.

### Commit messages

- Use the imperative mood in the subject line: "Add OPC UA reconnect backoff", not "Added…"
- Keep the subject line under about 72 characters.
- Explain *why* in the body when the change isn't obvious.
- Reference related issues, for example `Fixes #42`.

## Developer Certificate of Origin (DCO)

Pulaski uses the [Developer Certificate of Origin](https://developercertificate.org/) instead of a Contributor License Agreement. By signing off a commit, you certify that you wrote the change or otherwise have the right to submit it under the project's license.

Every commit must include a `Signed-off-by` line matching your Git name and email:

```
Signed-off-by: Jane Doe <jane.doe@example.com>
```

Git adds this for you with the `-s` flag:

```bash
git commit -s -m "Add Sparkplug B birth certificate parsing"
```

**Forgot to sign off?**

```bash
# Fix the most recent commit
git commit --amend -s --no-edit

# Fix every commit on your branch
git rebase --signoff main

# Then update your pull request
git push --force-with-lease
```

Pull requests with unsigned commits cannot be merged.

## Pull request process

1. Open the pull request against `main` and link the related issue.
2. Describe what changed, why, and how you tested it.
3. A maintainer will review it. Expect questions and requested changes; that is a normal part of the process.
4. Once approved and all checks pass, a maintainer will merge it.

## Reporting security issues

Please **do not** open a public issue for security vulnerabilities. Contact the maintainer privately instead. A `SECURITY.md` with reporting details will be added before the first release.

## Conduct

Be respectful, constructive, and patient. Assume good intent, critique ideas rather than people, and remember that contributors come from very different backgrounds, from software engineering to the plant floor. A formal code of conduct will be added as the community grows.

## License

By contributing to Pulaski, you agree that your contributions are licensed under the [Apache License, Version 2.0](LICENSE.md), as described in Section 5 of the license.
