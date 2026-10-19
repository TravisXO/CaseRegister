# Third-party notices

This project is an independent concept and is not affiliated with ZIEA. It includes the following material from others.

## The list of most used breached passwords

`CaseRegister.Infrastructure/Security/Data/ncsc-100k-passwords.txt.gz` is a gzipped copy of `100k-most-used-passwords-NCSC.txt` from the **SecLists** project (`Passwords/Common-Credentials/`), used only when Have I Been Pwned cannot be reached (see `ResilientBreachedPasswordChecker`). One empty line was dropped; nothing else was changed.

- **SecLists**, by Daniel Miessler and Jason Haddix, is licensed under the **MIT License**. Copyright (c) 2018 Daniel Miessler. <https://github.com/danielmiessler/SecLists>
- The list was originally published by the **UK National Cyber Security Centre (NCSC)** and contains public sector information licensed under the **Open Government Licence v3.0**. Contains public sector information licensed under the Open Government Licence v3.0. <https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/> Source: National Cyber Security Centre, "100k most used passwords" (<https://www.ncsc.gov.uk/blog-post/passwords-passwords-everywhere>).

The MIT License permits use, copying, modification, merging, publishing, distribution, sublicensing and sale, provided the copyright notice and permission notice are included in all copies or substantial portions of the work. The Software is provided "as is", without warranty of any kind.

## Packages

The packages the project depends on are listed in `Directory.Packages.props` (backend) and `frontend/package.json`; their licences were checked when each was added. See `docs/frontend.md` for the front end's.

### Development tools added for the accessibility checks

None of these is part of what is built or served; they only run in tests and in CI.

| Package | Licence | Used for |
| --- | --- | --- |
| `vitest-axe` 0.1.0 | MIT | the matcher that reports axe's findings in the screen tests |
| `axe-core` 4.14 (already used) | MPL-2.0 (file-level terms; used unmodified, as a tool) | the accessibility rules |
| `@playwright/test` 1.64 | Apache-2.0 | drives the browser in `frontend/e2e` |
| `@axe-core/playwright` 4.14 | MPL-2.0 (used unmodified, as a tool) | axe inside the browser checks |
| `@lhci/cli` 0.15.1 (run with `npx`, not a dependency) | Apache-2.0 | Lighthouse CI |

