# SABCTools yEnc Fixture Provenance

This directory contains upstream yEnc test fixtures copied from the `sabnzbd/sabctools` repository for offline, reproducible tests.

- Upstream repository: `https://github.com/sabnzbd/sabctools`
- Upstream fixture path: `tests/yencfiles`

## Included fixture files

- `test_regular.yenc`
- `test_regular_2.yenc`
- `test_special_chars.yenc`
- `test_special_utf8_chars.yenc`
- `test_partial.yenc`
- `test_bad_crc.yenc`
- `test_bad_crc_end.yenc`
- `test_invalid_crc_chars.yenc`
- `test_invalid_escape.yenc`
- `test_missing_yend.yenc`
- `test_malformed_ybegin.yenc`
- `test_ypart_without_ybegin.yenc`
- `test_empty_file.yenc`

These fixture files are preserved as upstream artifacts and are not rewritten by tests.
