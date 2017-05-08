﻿using DiscUtils.Internal;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Packaging.Targets.IO;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Packaging.Targets.Rpm
{
    /// <summary>
    /// Supports creating <see cref="RpmPackage"/> objects based on a <see cref="CpioFile"/>.
    /// </summary>
    internal class RpmPackageCreator
    {
        /// <summary>
        /// The <see cref="IFileAnalyzer"/> which analyzes the files in this package and provides required
        /// metadata for the files.
        /// </summary>
        private readonly IFileAnalyzer analyzer;

        /// <summary>
        /// Initializes a new instance of the <see cref="RpmPackageCreator"/> class.
        /// </summary>
        public RpmPackageCreator()
            : this(new FileAnalyzer())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RpmPackageCreator"/> class.
        /// </summary>
        /// <param name="analyzer">
        /// An <see cref="IFileAnalyzer"/> used to analyze the files in this package and provides required
        /// meatata for the files.
        /// </param>
        public RpmPackageCreator(IFileAnalyzer analyzer)
        {
            if (analyzer == null)
            {
                throw new ArgumentNullException(nameof(analyzer));
            }

            this.analyzer = analyzer;
        }

        /// <summary>
        /// Creates the metadata for all files in the <see cref="CpioFile"/>.
        /// </summary>
        /// <param name="payload">
        /// The payload for which to generate the metadata.
        /// </param>
        /// <returns>
        /// A <see cref="Collection{RpmFile}"/> which contains all the metadata.
        /// </returns>
        public Collection<RpmFile> CreateFiles(CpioFile payload)
        {
            Collection<RpmFile> files = new Collection<RpmFile>();

            while (payload.Read())
            {
                byte[] hash;
                byte[] buffer = new byte[1024];
                byte[] header = null;
                int read = 0;

                using (var stream = payload.Open())
                using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    while (true)
                    {
                        read = stream.Read(buffer, 0, buffer.Length);

                        if (header == null)
                        {
                            header = new byte[read];
                            Buffer.BlockCopy(buffer, 0, header, 0, read);
                        }

                        hasher.AppendData(buffer, 0, read);

                        if (read < buffer.Length)
                        {
                            break;
                        }
                    }

                    hash = hasher.GetHashAndReset();
                }

                string fileName = payload.EntryName;
                int fileSize = (int)payload.EntryHeader.FileSize;

                if (fileName.StartsWith("."))
                {
                    fileName = fileName.Substring(1);
                }

                string linkTo = string.Empty;

                if (payload.EntryHeader.Mode.HasFlag(LinuxFileMode.S_IFLNK))
                {
                    // Find the link text
                    int stringEnd = 0;

                    while (stringEnd < header.Length - 1 && header[stringEnd] != 0)
                    {
                        stringEnd++;
                    }

                    linkTo = Encoding.UTF8.GetString(header, 0, stringEnd + 1);
                    hash = new byte[] { };
                }
                else if (payload.EntryHeader.Mode.HasFlag(LinuxFileMode.S_IFDIR))
                {
                    fileSize = 0x00001000;
                    hash = new byte[] { };
                }

                RpmFile file = new RpmFile()
                {
                    Size = fileSize,
                    Mode = payload.EntryHeader.Mode,
                    Rdev = (short)payload.EntryHeader.RDevMajor,
                    ModifiedTime = payload.EntryHeader.Mtime,
                    MD5Hash = hash,
                    LinkTo = linkTo,
                    Flags = this.analyzer.DetermineFlags(fileName, payload.EntryHeader, header),
                    UserName = "root",
                    GroupName = "root",
                    VerifyFlags = RpmVerifyFlags.RPMVERIFY_ALL,
                    Device = 1,
                    Inode = (int)payload.EntryHeader.Ino,
                    Lang = "",
                    Color = this.analyzer.DetermineColor(fileName, payload.EntryHeader, header),
                    Class = this.analyzer.DetermineClass(fileName, payload.EntryHeader, header),
                    Requires = this.analyzer.DetermineRequires(fileName, payload.EntryHeader, header),
                    Provides = this.analyzer.DetermineProvides(fileName, payload.EntryHeader, header),
                    Name = fileName
                };

                files.Add(file);
            }

            return files;
        }

        /// <summary>
        /// Adds the package-level provides to the metadata. These are basically statements
        /// indicating that the package provides, well, itself.
        /// </summary>
        /// <param name="metadata">
        /// The package to which to add the provides.
        /// </param>
        public void AddPackageProvides(RpmMetadata metadata)
        {
            var provides = metadata.Provides.ToList();

            var packageProvides = new PackageDependency(metadata.Name, RpmSense.RPMSENSE_EQUAL, $"{metadata.Version}-{metadata.Release}");

            var normalizedArch = metadata.Arch;
            if (normalizedArch == "x86_64")
            {
                normalizedArch = "x86-64";
            }

            var packageArchProvides = new PackageDependency($"{metadata.Name}({normalizedArch})", RpmSense.RPMSENSE_EQUAL, $"{metadata.Version}-{metadata.Release}");

            if (!provides.Contains(packageProvides))
            {
                provides.Add(packageProvides);
            }

            if (!provides.Contains(packageArchProvides))
            {
                provides.Add(packageArchProvides);
            }

            metadata.Provides = provides;
        }

        /// <summary>
        /// Adds the dependency on ld to the RPM package. These dependencies cause <c>ldconfig</c> to run post installation
        /// and uninstallation of the RPM package.
        /// </summary>
        /// <param name="metadata">
        /// The <see cref="RpmMetadata"/> to which to add the dependencies.
        /// </param>
        public void AddLdDependencies(RpmMetadata metadata)
        {
            Collection<PackageDependency> ldDependencies = new Collection<PackageDependency>()
            {
                new PackageDependency("/sbin/ldconfig", RpmSense.RPMSENSE_INTERP | RpmSense.RPMSENSE_SCRIPT_POST, string.Empty),
                new PackageDependency("/sbin/ldconfig", RpmSense.RPMSENSE_INTERP | RpmSense.RPMSENSE_SCRIPT_POSTUN,string.Empty)
            };

            var dependencies = metadata.Dependencies.ToList();
            dependencies.AddRange(ldDependencies);
            metadata.Dependencies = dependencies;
        }

        /// <summary>
        /// Adds the RPM dependencies to the package. These dependencies express dependencies on specific RPM features, such as compressed file names,
        /// file digets, and xz-compressed payloads.
        /// </summary>
        /// <param name="metadata">
        /// The <see cref="RpmMetadata"/> to which to add the dependencies.
        /// </param>
        public void AddRpmDependencies(RpmMetadata metadata)
        {
            // Somehow, three rpmlib dependencies come before the rtld(GNU_HASH) dependency and one after.
            // The rtld(GNU_HASH) indicates that hashes are stored in the .gnu_hash instead of the .hash section
            // in the ELF file, so it is a file-level dependency that bubbles up
            // http://lists.rpm.org/pipermail/rpm-maint/2014-September/003764.html
            //
            // To work around it, we remove the rtld(GNU_HASH) dependency on the files, remove it as a dependency,
            // and add it back once we're done.
            //
            // The sole purpose of this is to ensure binary compatibility, which is probably not required at runtime,
            // but makes certain regression tests more stable.
            //
            // Here we go:
            var files = metadata.Files.ToArray();

            var gnuHashFiles =
                files
                .Where(f => f.Requires.Any(r => string.Equals(r.Name, "rtld(GNU_HASH)", StringComparison.Ordinal)))
                .ToArray();

            foreach (var file in gnuHashFiles)
            {
                var rtldDependency = file.Requires.Where(r => string.Equals(r.Name, "rtld(GNU_HASH)", StringComparison.Ordinal)).Single();
                file.Requires.Remove(rtldDependency);
            }

            // Refresh
            metadata.Files = files;

            Collection<PackageDependency> rpmDependencies = new Collection<PackageDependency>()
            {
                new PackageDependency("rpmlib(CompressedFileNames)",RpmSense.RPMSENSE_LESS | RpmSense.RPMSENSE_EQUAL | RpmSense.RPMSENSE_RPMLIB, "3.0.4-1"),
                new PackageDependency("rpmlib(FileDigests)",RpmSense.RPMSENSE_LESS | RpmSense.RPMSENSE_EQUAL | RpmSense.RPMSENSE_RPMLIB, "4.6.0-1"),
                new PackageDependency("rpmlib(PayloadFilesHavePrefix)", RpmSense.RPMSENSE_LESS | RpmSense.RPMSENSE_EQUAL | RpmSense.RPMSENSE_RPMLIB,"4.0-1"),
                new PackageDependency("rtld(GNU_HASH)",RpmSense.RPMSENSE_FIND_REQUIRES, string.Empty),
                new PackageDependency("rpmlib(PayloadIsXz)", RpmSense.RPMSENSE_LESS | RpmSense.RPMSENSE_EQUAL | RpmSense.RPMSENSE_RPMLIB, "5.2-1")
            };

            var dependencies = metadata.Dependencies.ToList();
            var last = dependencies.Last();

            if (last.Name == "rtld(GNU_HASH)")
            {
                dependencies.Remove(last);
            }

            dependencies.AddRange(rpmDependencies);
            metadata.Dependencies = dependencies;

            // Add the rtld(GNU_HASH) dependency back to the files
            foreach (var file in gnuHashFiles)
            {
                file.Requires.Add(new PackageDependency("rtld(GNU_HASH)", RpmSense.RPMSENSE_FIND_REQUIRES, string.Empty));
            }

            // Refresh
            metadata.Files = files;
        }

        /// <summary>
        /// Determines the offsets for all records in the header of a package.
        /// </summary>
        /// <param name="package">
        /// The package for which to generate the offsets.
        /// </param>
        public void CalculateHeaderOffsets(RpmPackage package)
        {
            var metadata = new RpmMetadata(package);
            metadata.ImmutableRegionSize = -1 * Marshal.SizeOf<IndexHeader>() * (package.Header.Records.Count + 1);

            CalculateSectionOffsets(package.Header, k => (int)k);
        }

        /// <summary>
        /// Determines the offsets for all records in the signature of a package.
        /// </summary>
        /// <param name="package">
        /// The package for which to generate the offsets.
        /// </param>
        public void CalculateSignatureOffsets(RpmPackage package)
        {
            var signature = new RpmSignature(package);
            signature.ImmutableRegionSize = -1 * Marshal.SizeOf<IndexHeader>() * (package.Signature.Records.Count + 1);

            CalculateSectionOffsets(package.Signature, k => (int)k);
        }

        /// <summary>
        /// Gets a <see cref="MemoryStream"/> wich represents the entire header.
        /// </summary>
        /// <param name="package">
        /// The package for which to get the header stream.
        /// </param>
        /// <returns>
        /// A <see cref="MemoryStream"/> wich represents the entire header.
        /// </returns>
        public MemoryStream GetHeaderStream(RpmPackage package)
        {
            MemoryStream stream = new MemoryStream();
            RpmPackageWriter.WriteSection(stream, package.Header, DefaultOrder.Header);
            stream.Position = 0;
            return stream;
        }

        /// <summary>
        /// Gets a <see cref="MemoryStream"/> wich represents the entire signature.
        /// </summary>
        /// <param name="package">
        /// The package for which to get the header stream.
        /// </param>
        /// <returns>
        /// A <see cref="MemoryStream"/> wich represents the entire signature.
        /// </returns>
        public MemoryStream GetSignatureStream(RpmPackage package)
        {
            MemoryStream stream = new MemoryStream();
            RpmPackageWriter.WriteSection(stream, package.Signature, DefaultOrder.Signature);
            stream.Position = 0;
            return stream;
        }

        /// <summary>
        /// Calculates the signature for this package.
        /// </summary>
        /// <param name="package">
        /// The package for whcih to calculate the signature.
        /// </param>
        /// <param name="privateKey">
        /// The private key to use.
        /// </param>
        /// <param name="compressedPayloadStream">
        /// The compressed payload.
        /// </param>
        public void CalculateSignature(RpmPackage package, PgpPrivateKey privateKey, Stream compressedPayloadStream)
        {
            RpmSignature signature = new RpmSignature(package);

            using (MemoryStream headerStream = this.GetHeaderStream(package))
            using (ConcatStream headerAndPayloadStream = new ConcatStream(headerStream, compressedPayloadStream))
            {
                SHA1 sha = SHA1.Create();
                signature.Sha1Hash = sha.ComputeHash(headerStream);

                MD5 md5 = MD5.Create();
                signature.MD5Hash = md5.ComputeHash(headerAndPayloadStream);

                // Verify the PGP signatures
                // 3 for the header
                headerStream.Position = 0;
                signature.HeaderPgpSignature = PgpSigner.Sign(privateKey, headerStream);

                headerAndPayloadStream.Position = 0;
                signature.HeaderAndPayloadPgpSignature = PgpSigner.Sign(privateKey, headerAndPayloadStream);

                // Verify the signature size (header + compressed payload)
                signature.HeaderAndPayloadSize = (int)headerAndPayloadStream.Length;
            }

            // Verify the payload size (header + uncompressed payload)

            using (Stream payloadStream = RpmPayloadReader.GetDecompressedPayloadStream(package, compressedPayloadStream))
            {
                signature.UncompressedPayloadSize = (int)payloadStream.Length;
            }
        }

        protected void CalculateSectionOffsets<T>(Section<T> section, Func<T, int> getSortOrder)
        {
            var records = section.Records;
            int offset = 0;

            T immutableKey = default(T);

            var sortedRecords = records.OrderBy(r => getSortOrder(r.Key)).ToArray();

            foreach (var record in sortedRecords)
            {
                var indexTag = record.Key as IndexTag?;
                var signatureTag = record.Key as SignatureTag?;

                if (indexTag == IndexTag.RPMTAG_HEADERIMMUTABLE
                    || signatureTag == SignatureTag.RPMTAG_HEADERSIGNATURES)
                {
                    immutableKey = record.Key;
                    continue;
                }

                var header = record.Value.Header;

                // Determine the size
                int size = 0;
                int align = 0;

                switch (header.Type)
                {
                    case IndexType.RPM_BIN_TYPE:
                    case IndexType.RPM_CHAR_TYPE:
                    case IndexType.RPM_INT8_TYPE:
                        // These are all single-byte types
                        size = header.Count;
                        break;

                    case IndexType.RPM_I18NSTRING_TYPE:
                    case IndexType.RPM_STRING_ARRAY_TYPE:
                        foreach (var value in (IEnumerable<string>)record.Value.Value)
                        {
                            size += Encoding.UTF8.GetByteCount(value) + 1;
                        }
                        break;

                    case IndexType.RPM_STRING_TYPE:
                        size = Encoding.UTF8.GetByteCount((string)record.Value.Value) + 1;
                        break;

                    case IndexType.RPM_INT16_TYPE:
                        size = 2 * header.Count;
                        break;

                    case IndexType.RPM_INT32_TYPE:
                        size = 4 * header.Count;
                        align = 4;
                        break;

                    case IndexType.RPM_INT64_TYPE:
                        size = 8 * header.Count;
                        break;

                    case IndexType.RPM_NULL_TYPE:
                        size = 0;
                        break;
                }

                if (align != 0 && offset % align != 0)
                {
                    offset += align - (offset % align);
                }

                header.Offset = offset;
                offset += size;

                record.Value.Header = header;
            }

            if (!object.Equals(immutableKey, default(T)))
            {
                var record = records[immutableKey];
                var header = record.Header;
                header.Offset = offset;
                record.Header = header;

                offset += Marshal.SizeOf<IndexHeader>();
            }

            // This is also a good time to refresh the header
            section.Header = new RpmHeader()
            {
                HeaderSize = (uint)offset,
                IndexCount = (uint)section.Records.Count,
                Magic = 0x8eade801,
                Reserved = 0
            };
        }
    }
}
