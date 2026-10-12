using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Test.Common;

namespace NzbDrone.Windows.Test
{
    [TestFixture]
    [Platform("Win")]
    public class ShortPathFixture : TestBase
    {
        private string _root;
        private string _libraryPath;
        private string _moviePath;

        [SetUp]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "Radarr short path test " + Guid.NewGuid()).CleanFilePath();
            _libraryPath = Path.Combine(_root, "Movie Library");
            _moviePath = Path.Combine(_libraryPath, "Example Film (2000)", "Example Film 2000.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(_moviePath));
            File.WriteAllText(_moviePath, "movie");
        }

        [TearDown]
        public void Cleanup()
        {
            DeleteTempFolder(_root);
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void should_preserve_relative_movie_path_between_short_and_long_names(bool shortParent, bool shortChild)
        {
            var parent = shortParent ? ToShortPath(_libraryPath) : _libraryPath;
            var child = shortChild ? ToShortPath(_moviePath) : _moviePath;

            parent.IsParentPath(child).Should().BeTrue();
            parent.GetRelativePath(child).Should().Be(Path.Combine("Example Film (2000)", "Example Film 2000.mkv"));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_not_treat_a_sibling_as_part_of_the_library(bool shortParent)
        {
            var sibling = Path.Combine(_root, "Movie Library Extra");
            Directory.CreateDirectory(sibling);
            var parent = shortParent ? ToShortPath(_libraryPath) : _libraryPath;
            var child = Path.Combine(shortParent ? sibling : ToShortPath(sibling), "Example Film 2000.mkv");

            parent.IsParentPath(child).Should().BeFalse();
            Action getRelativePath = () => parent.GetRelativePath(child);
            getRelativePath.Should().Throw<NotParentException>();
        }

        [Test]
        public void should_resolve_a_destination_before_the_file_exists()
        {
            var destination = Path.Combine(ToShortPath(_libraryPath), "Another Film (2001)", "Another Film 2001.mkv");

            _libraryPath.IsParentPath(destination).Should().BeTrue();
            _libraryPath.GetRelativePath(destination).Should().Be(Path.Combine("Another Film (2001)", "Another Film 2001.mkv"));
        }

        [Test]
        public void should_preserve_a_literal_tilde_in_a_filename()
        {
            var destination = Path.Combine(_libraryPath, "Example Film (2000)", "Example ~ Film 2000.mkv");

            _libraryPath.IsParentPath(destination).Should().BeTrue();
            _libraryPath.GetRelativePath(destination).Should().Be(Path.Combine("Example Film (2000)", "Example ~ Film 2000.mkv"));
        }

        [Test]
        public void should_preserve_unix_paths_containing_a_tilde()
        {
            const string parent = "/remote/Movies ~ Library";
            const string child = "/remote/Movies ~ Library/Example Film (2000)/Example Film 2000.mkv";

            parent.IsParentPath(child).Should().BeTrue();
            parent.GetRelativePath(child).Should().Be("Example Film (2000)/Example Film 2000.mkv");
        }

        private static string ToShortPath(string path)
        {
            var buffer = new char[32768];
            var length = GetShortPathName(path, buffer, (uint)buffer.Length);
            if (length == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            length.Should().BeLessThan((uint)buffer.Length);
            var shortPath = new string(buffer, 0, (int)length);
            if (string.Equals(shortPath, path, StringComparison.OrdinalIgnoreCase))
            {
                Assert.Ignore("This volume does not provide short file names.");
            }

            return shortPath;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetShortPathName(string path, [Out] char[] buffer, uint size);
    }
}
