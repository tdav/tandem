# External archive fixtures

These archives cannot be made with the tools on a stock Mac (no `rar`, no `7z`/`7zz`; `bsdtar` cannot encrypt 7z), so
they are not in the repository. The tests that need them are skipped, and the skip reason names the missing file.
To run those tests, put the files here under these names:

| File here | What it must be | Candidates in the SharpCompress test archives |
|---|---|---|
| `sample.rar` | A plain (non-solid) RAR archive | `Rar.rar`, `Rar5.rar` |
| `solid.rar` | A solid RAR archive | `Rar.solid.rar`, `Rar5.solid.rar` |
| `encrypted.7z` | A 7z archive with **encrypted headers**: listing it without a password must fail | `7Zip.LZMA.Aes.7z`, `7Zip.LZMA2.Aes.7z` (`7Zip.encryptedFiles.7z` keeps its header in the clear, so it does not qualify) |

Where to get them: the folder `tests/TestArchives/Archives` of the SharpCompress repository
(github.com/adamhathcock/sharpcompress), MIT licence. The probes open `encrypted.7z` with the password `testpassword`
(`Fixture.External7zPassword`). That is believed to be the one the SharpCompress tests use for their AES archives, but
check it in that repository's `tests` folder. If your file has another password, change the constant.

## What each test expects

No test expects a particular entry name or content from these files: they do not hold the common tree, so the tests
check what any such archive must satisfy.

| Test | File | Checks |
|---|---|---|
| `ArchiveFormatsTests.Detects_by_signature` | all three | `sample.rar` and `solid.rar` are detected as `Rar`, `encrypted.7z` as `SevenZip`. |
| `SharpCompressProbeTests.Rar_lists_and_reads` | `sample.rar`, `solid.rar` | `ArchiveType.Rar`; at least one file; every file reads exactly its listed `Size` bytes. |
| `SharpCompressProbeTests.Solid_archives_extract_all_entries_in_one_pass` | `solid.rar` | `IArchive.IsSolid` is true; one `ExtractAllEntries` pass yields every listed file with its listed size. |
| `SharpCompressProbeTests.Encrypted_entries_read_with_the_right_password` | `encrypted.7z` | Opened with `testpassword`: at least one file; every file reads exactly its listed `Size` bytes. |
| `SharpCompressProbeTests.Header_encrypted_7z_does_not_list_without_password` | `encrypted.7z` | Listing without a password throws (any exception: the type is to be recorded once the file is here); with `testpassword` the listing is not empty. |

Task 23 (`Encrypted_7z_asks_for_password_at_mount`) uses the same file and password.

When you add a file, also record here its original name, the repository commit it came from, the password (for
`encrypted.7z`) and the name of one file entry in it with its content, so the probe tests can check what they read.
