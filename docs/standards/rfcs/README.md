# RFC standards reference library

This directory holds **official plain-text copies** of RFCs used as the persistent standards reference library for `VectorNNTP.NNTPD`.

Use these files for implementation, design reviews, and conformance testing. Local copies are for convenience and offline review only. **Implementation decisions must still be checked against the applicable RFC text, subsequent updates, and errata** published by the RFC Editor / IETF.

Source for every file: [RFC Editor](https://www.rfc-editor.org/) plain-text URLs of the form `https://www.rfc-editor.org/rfc/rfc<N>.txt`.

Status, obsolescence, and update relationships below were verified from RFC Editor metadata (`https://www.rfc-editor.org/rfc/rfc<N>.json`) at download time. Do not assume every listed RFC is current or independently applicable to NNTP.

## Collection

| RFC | Title | Status (RFC Editor) | Local file | Official URL | Notes |
|-----|-------|---------------------|------------|--------------|-------|
| 977 | Network News Transfer Protocol | Proposed Standard (**Obsoleted**) | [`rfc977.txt`](rfc977.txt) | https://www.rfc-editor.org/rfc/rfc977.txt | **Obsoleted by RFC 3977.** Historical NNTP base specification (1986). |
| 1738 | Uniform Resource Locators (URL) | Proposed Standard (**Obsoleted**) | [`rfc1738.txt`](rfc1738.txt) | https://www.rfc-editor.org/rfc/rfc1738.txt | **Obsoleted by RFC 4248 and RFC 4266.** Updated by several later URI RFCs. Retained for historical URL context; `news` / `nntp` URI schemes are specified in RFC 5538. |
| 2980 | Common NNTP Extensions | Informational | [`rfc2980.txt`](rfc2980.txt) | https://www.rfc-editor.org/rfc/rfc2980.txt | Informational extensions commonly implemented with NNTP. **Updated by** RFC 3977, RFC 4643, RFC 4644, and RFC 6048. |
| 3977 | Network News Transfer Protocol (NNTP) | Proposed Standard | [`rfc3977.txt`](rfc3977.txt) | https://www.rfc-editor.org/rfc/rfc3977.txt | Current NNTP base protocol. **Obsoletes** RFC 977. **Updates** RFC 2980. **Updated by** RFC 6048. |
| 4642 | Using Transport Layer Security (TLS) with Network News Transfer Protocol (NNTP) | Proposed Standard | [`rfc4642.txt`](rfc4642.txt) | https://www.rfc-editor.org/rfc/rfc4642.txt | TLS usage with NNTP. **Updated by** RFC 8143 and RFC 8996. |
| 4643 | Network News Transfer Protocol (NNTP) Extension for Authentication | Proposed Standard | [`rfc4643.txt`](rfc4643.txt) | https://www.rfc-editor.org/rfc/rfc4643.txt | Authentication extension. **Updates** RFC 2980. |
| 4644 | Network News Transfer Protocol (NNTP) Extension for Streaming Feeds | Proposed Standard | [`rfc4644.txt`](rfc4644.txt) | https://www.rfc-editor.org/rfc/rfc4644.txt | Streaming feeds extension. **Updates** RFC 2980. |
| 1036 | Standard for interchange of USENET messages | Historic (**Obsoleted**) | [`rfc1036.txt`](rfc1036.txt) | https://www.rfc-editor.org/rfc/rfc1036.txt | **Obsoleted by RFC 5536 and RFC 5537.** Historical Usenet message format and control conventions only. Do not implement CANCEL or Control semantics from this document. |
| 5536 | Netnews Article Format | Proposed Standard | [`rfc5536.txt`](rfc5536.txt) | https://www.rfc-editor.org/rfc/rfc5536.txt | Current Netnews article format, including Control header syntax. **Obsoletes** RFC 1036. |
| 5537 | Netnews Architecture and Protocols | Proposed Standard | [`rfc5537.txt`](rfc5537.txt) | https://www.rfc-editor.org/rfc/rfc5537.txt | Current Netnews architecture, including CANCEL (`Control: cancel <message-id>`). **Obsoletes** RFC 1036. RFC 5537 removed the obsolete RFC 1036 `cmsg` Subject convention. RFC 5537 §5.1 does **not** standardize control-message authentication; it cites PGPVERIFY as an unstandardized mechanism. |
| 5538 | The 'news' and 'nntp' URI Schemes | Proposed Standard | [`rfc5538.txt`](rfc5538.txt) | https://www.rfc-editor.org/rfc/rfc5538.txt | `news` and `nntp` URI schemes. |
| 6048 | Network News Transfer Protocol (NNTP) Additions to LIST Command | Proposed Standard | [`rfc6048.txt`](rfc6048.txt) | https://www.rfc-editor.org/rfc/rfc6048.txt | LIST command additions. **Updates** RFC 2980 and RFC 3977. |
| 8054 | Network News Transfer Protocol (NNTP) Extension for Compression | Proposed Standard | [`rfc8054.txt`](rfc8054.txt) | https://www.rfc-editor.org/rfc/rfc8054.txt | Compression extension for NNTP. |
| 8143 | Using Transport Layer Security (TLS) with Network News Transfer Protocol (NNTP) | Proposed Standard | [`rfc8143.txt`](rfc8143.txt) | https://www.rfc-editor.org/rfc/rfc8143.txt | Updates TLS-with-NNTP guidance. **Updates** RFC 4642. |

## Relationship overview (NNTP-focused)

```text
RFC 977  --(obsoleted by)-->  RFC 3977  <--(updated by)-- RFC 6048
                                 |
                                 +-- updates --> RFC 2980
                                       ^
                                       |
              RFC 4643, RFC 4644, RFC 3977, RFC 6048 also update RFC 2980

RFC 1036  --(obsoleted by)--> RFC 5536 + RFC 5537
                                 |              |
                                 |              +-- CANCEL / control-message protocol
                                 +-- Netnews article format (Control syntax)

PGPVERIFY  --(not an RFC)--> de-facto control-message authentication
                             See ../pgpverify/ (FORMAT + README).
                             VectorNNTP implements it for CANCEL interoperability.
                             Do not describe PGPVERIFY as RFC-compliant.
RFC 4642  --(updated by)--> RFC 8143 (and RFC 8996)
RFC 5538  defines news/nntp URI schemes (see also historical RFC 1738)
RFC 8054  NNTP compression extension
```

## SHA-256 checksums

Computed after download from the RFC Editor (lowercase hex):

| File | Bytes | SHA-256 |
|------|------:|---------|
| `rfc1036.txt` | 45825 | `712c774b227d9f7d1f9fe8a49e7af58efdc77a68fbd5b9ecd51c574de933d3f6` |
| `rfc977.txt` | 53523 | `c6c987a0a0d832344d66f07c69f13398705dd23467df205c51a97609f5bc98ae` |
| `rfc1738.txt` | 51348 | `2622f97a21c8b28558e860c2ad53a6f3114ac0cf905eb51aefa38e0629d76b2d` |
| `rfc2980.txt` | 57165 | `a08a7c72ba79053a53aee51dbdeb4d31a38342418d287f08e20acb2238c13600` |
| `rfc3977.txt` | 247440 | `e9bafe47d8c9b1136f11546162599ee67c233f33955c2972b57a91ecaff1f5f5` |
| `rfc4642.txt` | 29366 | `b6abf6a451078c0958488d5a0e5ef6a29e8dca2c2237e351a582395b320895f6` |
| `rfc4643.txt` | 51411 | `e68fa965e1b50b154ce807edfabe1a35ea97e2d9371cc4832602be69e5507126` |
| `rfc4644.txt` | 26438 | `452fc4ff0b23d66c66dbaa4433e1ba8564217cac047ec8ce0b765b6f8d4bda2b` |
| `rfc5536.txt` | 71817 | `141e122bd171ba414ed5afecec6073aa2db4e9f90119e2cde362c2c94ccb6733` |
| `rfc5537.txt` | 120366 | `d729e7a4e84006d1ada9945e7fa011e3c8f6600700217b7b3230e8d78116683b` |
| `rfc5538.txt` | 29470 | `45ced1d2ad2dc503b94c888640179c71d08a9f7ffe669d9adb895697c5b54dd4` |
| `rfc6048.txt` | 50873 | `6e7db9ac0b56c2276b1e1215d6998b406c57a97de31e7a1405223e4ef3f00a6a` |
| `rfc8054.txt` | 46113 | `63f58e6675debaaaa02c7c77ba2907104903ccb9fc9461497fd36ece4a9bdbdb` |
| `rfc8143.txt` | 28484 | `f219e3760fe8de93b448e9fb239ea102115ff18445c00efdb783c523961dbf27` |

## Verification notes

- Each file was retrieved with HTTP 200 from `https://www.rfc-editor.org/rfc/rfc<N>.txt`.
- Content was checked to be non-empty plain text (not an HTML error page) and to declare the matching `Request for Comments: <N>` header.
- Files are stored verbatim; they were not reformatted, summarized, or combined.
- Consult [RFC Editor errata](https://www.rfc-editor.org/errata.php) for approved corrections after reading the local text.
