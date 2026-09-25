# INN pgpverify 1.31 (interop fixture)

## Provenance

| Field | Value |
|-------|-------|
| Repository | [InterNetNews/inn](https://github.com/InterNetNews/inn) |
| Source path | `control/pgpverify.in` |
| Upstream tag | `2.7.4` |
| Script version | 1.31, 2022-06-12 (David Lawrence / Russ Allbery) |
| Retrieval URL | https://raw.githubusercontent.com/InterNetNews/inn/2.7.4/control/pgpverify.in |
| SHA-256 | `a1da4617020194ffe74d8f50b8f289e47f176a9d91b008b7b207bac562901a51` |

`pgpverify.in` is stored verbatim from that URL. Tests copy it to a temporary directory and inject standalone `$gpg` / `$tmpdir` / `$keyring` assignments. The fixture file itself is not modified.

This is **not** a full INN installation. Only the standalone pgpverify script is used.

## License / attribution

The script retains its upstream copyright notices (UUNET / David Lawrence; later INN maintainers). INN as a whole is covered by the ISC-style license documented with the INN article corpus at `tests/VectorNNTP.NNTPD.Tests/Data/InnArticles/LICENSE.ISC.txt`.
