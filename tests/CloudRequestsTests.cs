namespace MailRuCloudClient.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Threading.Tasks;

    using MailRuCloudClient;
    using MailRuCloudClient.Data;
    using MailRuCloudClient.Events;
    using MailRuCloudClient.Exceptions;

    using NUnit.Framework;
    using NUnit.Framework.Constraints;

    [TestFixture]
    public class CloudRequestsTests
    {
        public const string Login = "tencryption@mail.ru";
        public const string Password = "TFh4^cq:b'wJRoUk&Z=i";

        private Account account = null;
        private CloudClient client = null;

        private const string TestFolderName = "new folder"; // In Cloud
        private const string TestFolderPath = "/" + TestFolderName; // In Cloud
        private const string TestFolderPublicLink = "https://cloud.mail.ru/public/JWXJ/xsyPB2eZU"; // In Cloud
        private const string TestFileName = "black_sabbath-iron_man_11.gp5"; // The common file name
        private const string TestDownloadFilePath = TestFolderPath + "/" + TestFileName; // In Cloud
        private const string TestHistoryCheckingFilePath = "/Новая таблица.xlsx"; // In Cloud, this file need to create manually and fill history

        private int prevDownloadProgressPercentage = -1;

        private string TestFilePath
        {
            get
            {
                var codebase = System.Reflection.Assembly.GetExecutingAssembly().CodeBase;
                var assemblyPath = new Uri(codebase).LocalPath;
                var directoryPath = Path.GetDirectoryName(assemblyPath);
                return Path.Combine(directoryPath, "Files", TestFileName);
            }
        }

        [Test]
        public async Task OneTimeDirectLinkTest()
        {
            var file = await this.client.Publish<MailRuCloudClient.Data.File>(TestDownloadFilePath);
            var directLink = await this.client.GetFileOneTimeDirectLink(file.PublicLink);
            var httpClient = new HttpClient();
            var responseMsg = await httpClient.GetAsync(directLink);
            Assert.True(responseMsg.IsSuccessStatusCode);
        }

        [Test]
        public async Task PublishUnpublishTest()
        {
            var task = this.client.Publish<Folder>(TestFolderPath + "/" + Guid.NewGuid());
            var exception = Assert.ThrowsAsync<CloudClientException>(() => task);
            Assert.AreEqual(ErrorCode.PathNotExists, (ErrorCode)exception.HResult);
            Assert.AreEqual("sourceFullPath", exception.Source);

            task = this.client.Unpublish<Folder>(Guid.NewGuid().ToString());
            exception = Assert.ThrowsAsync<CloudClientException>(() => task);
            Assert.AreEqual(ErrorCode.PublicLinkNotExists, (ErrorCode)exception.HResult);
            Assert.AreEqual("link", exception.Source);

            var result = await this.client.Publish<MailRuCloudClient.Data.File>(TestDownloadFilePath);
            Assert.That(result.PublicLink, new StartsWithConstraint("https://cloud.mail.ru/public/"));

            result = await this.client.Unpublish<MailRuCloudClient.Data.File>(result.PublicLink);
            Assert.Null(result.PublicLink);
        }

        [Test]
        public async Task RatesTest()
        {
            foreach (var rate in this.account.ActivatedTariffs)
            {
                Assert.NotNull(rate.Name);
                Assert.NotNull(rate.Id);
                if (rate.Id == "ZERO")
                {
                    Assert.Null(rate.Cost);
                }
                else
                {
                    Assert.NotNull(rate.Cost);
                    foreach (var cost in rate.Cost)
                    {
                        Assert.True(cost.Cost > 0);
                        Assert.True(cost.SpecialCost > 0);
                        Assert.AreEqual("RUR", cost.Currency);
                        Assert.True(cost.Duration.DaysCount > 0 || cost.Duration.MonthsCount > 0);
                        Assert.True(cost.SpecialDuration.DaysCount > 0 || cost.SpecialDuration.MonthsCount > 0);
                        Assert.NotNull(cost.Id);
                    }
                }
            }
        }

        [Test]
        public async Task HistoryTest()
        {
            var task = this.client.GetFileHistory(TestFolderPath + "/" + Guid.NewGuid() + ".txt");
            var exception = Assert.ThrowsAsync<CloudClientException>(() => task);
            Assert.AreEqual(ErrorCode.PathNotExists, (ErrorCode)exception.HResult);
            Assert.AreEqual("sourceFullPath", exception.Source);

            var historyList = (await this.client.GetFileHistory(TestHistoryCheckingFilePath)).ToList();
            foreach (var history in historyList)
            {
                Assert.True(!string.IsNullOrEmpty(history.FullPath));
                Assert.True(!string.IsNullOrEmpty(history.Name));
                Assert.True(history.Id > 0);
                Assert.True(history.LastModifiedTimeUTC > default(DateTime));
                Assert.True(history.Size.DefaultValue > 0);
                Assert.True(historyList.IndexOf(history) != 0 ? !history.IsCurrentVersion : history.IsCurrentVersion);
                if (!this.account.Has2GBUploadSizeLimit)
                {
                    Assert.NotNull(history.Hash);
                    Assert.True(history.Revision > 0);
                }
            }

            var lastHistory = historyList.Last();
            if (this.account.Has2GBUploadSizeLimit)
            {
                var task2 = this.client.RestoreFileFromHistory(TestHistoryCheckingFilePath, lastHistory.Id, false);
                exception = Assert.ThrowsAsync<CloudClientException>(() => task2);
                Assert.AreEqual(ErrorCode.NotSupportedOperation, (ErrorCode)exception.HResult);
            }

            var task3 = this.client.RestoreFileFromHistory(TestHistoryCheckingFilePath, 12345678, false);
            exception = Assert.ThrowsAsync<CloudClientException>(() => task3);
            Assert.AreEqual(ErrorCode.HistoryNotExists, (ErrorCode)exception.HResult);
            Assert.AreEqual("historyRevision", exception.Source);

            var newFileName = Guid.NewGuid().ToString();
            var extension = Path.GetExtension(TestHistoryCheckingFilePath);
            var result = await this.client.RestoreFileFromHistory(TestHistoryCheckingFilePath, lastHistory.Revision, false, newFileName);
            Assert.AreEqual(newFileName + extension, result.Name);
            Assert.AreEqual(result.FullPath.Substring(0, newFileName.LastIndexOf("/") + 2) + newFileName + extension, result.FullPath);
            Assert.AreEqual(lastHistory.Size.DefaultValue, result.Size.DefaultValue);
            Assert.AreEqual(lastHistory.Hash, result.Hash);
            Assert.AreEqual(lastHistory.LastModifiedTimeUTC, result.LastModifiedTimeUTC);
        }

        [Test]
        public async Task RemoveTest()
        {
            var folder = await this.client.CreateFolder(TestFolderName + "/" + Guid.NewGuid());
            await this.client.Remove(folder.FullPath);
            Assert.Null(await this.client.GetFolder(folder.FullPath));
        }

        [Test]
        public async Task RenameTest()
        {
            var fileInfo = new FileInfo(this.TestFilePath);
            var file = await this.client.UploadFile(null, fileInfo.FullName, TestFolderPath);
            var folder = await this.client.CreateFolder(TestFolderName + "/" + Guid.NewGuid());

            var newFileName = Guid.NewGuid().ToString();
            var newFolderName = Guid.NewGuid().ToString();

            var task = this.client.Rename<Folder>(TestFolderPath + "/" + Guid.NewGuid(), newFolderName);
            var exception = Assert.ThrowsAsync<CloudClientException>(() => task);
            Assert.AreEqual(ErrorCode.PathNotExists, (ErrorCode)exception.HResult);
            Assert.AreEqual("sourceFullPath", exception.Source);

            var renamedFile = await file.Rename(newFileName);
            Assert.AreEqual(newFileName + Path.GetExtension(file.Name), renamedFile.Name);
            Assert.AreEqual(
                renamedFile.FullPath.Substring(0, renamedFile.FullPath.LastIndexOf("/") + 1) + newFileName + Path.GetExtension(file.Name), 
                renamedFile.FullPath);

            var renamedFolder = await folder.Rename(newFolderName);
            Assert.AreEqual(newFolderName, renamedFolder.Name);
            Assert.AreEqual(
                renamedFolder.FullPath.Substring(0, renamedFolder.FullPath.LastIndexOf("/") + 1) + newFolderName,
                renamedFolder.FullPath);
        }

        [Test]
        public async Task MoveCopyTest()
        {
            var moveCopyFolderName = Guid.NewGuid().ToString();
            var moveCopyFolderPath = TestFolderPath + "/" + moveCopyFolderName;
            var moveCopyFolder = await this.client.CreateFolder(moveCopyFolderPath);

            var fileExtension = Path.GetExtension(TestFileName);
            var moveCopyFileName = Guid.NewGuid().ToString();
            var moveCopyFile = await this.client.UploadFile(moveCopyFileName, this.TestFilePath, TestFolderPath);

            var moveCopyToFolderPath = TestFolderPath + "/" + Guid.NewGuid();
            var moveCopyToFolder = await this.client.CreateFolder(moveCopyToFolderPath);

            var task = this.client.Copy<Folder>(TestFolderPath + "/" + Guid.NewGuid(), moveCopyToFolderPath);
            var exception = Assert.ThrowsAsync<CloudClientException>(() => task);
            Assert.AreEqual(ErrorCode.PathNotExists, (ErrorCode)exception.HResult);
            Assert.AreEqual("sourceFullPath", exception.Source);

            task = this.client.Copy<Folder>(moveCopyFolderPath, TestFolderPath + "/" + Guid.NewGuid());
            exception = Assert.ThrowsAsync<CloudClientException>(() => task);
            Assert.AreEqual(ErrorCode.PathNotExists, (ErrorCode)exception.HResult);
            Assert.AreEqual("destFolderPath", exception.Source);

            var copiedFolder = await this.client.Copy<Folder>(moveCopyFolderPath, moveCopyToFolderPath);
            Assert.Null(copiedFolder.PublicLink);
            Assert.AreEqual(moveCopyToFolderPath + "/" + moveCopyFolderName, copiedFolder.FullPath);
            Assert.AreEqual(moveCopyFolderName, copiedFolder.Name);

            var movedFolder = await this.client.Move<Folder>(moveCopyFolderPath, moveCopyToFolderPath);
            Assert.Null(movedFolder.PublicLink);
            Assert.That(movedFolder.FullPath, new StartsWithConstraint(moveCopyToFolderPath + "/" + moveCopyFolderName));
            Assert.That(movedFolder.Name, new StartsWithConstraint(moveCopyFolderName));

            var copiedFile = await this.client.Copy<Data.File>(moveCopyFile.FullPath, moveCopyToFolderPath);
            Assert.Null(copiedFile.PublicLink);
            Assert.AreEqual(moveCopyToFolderPath + "/" + moveCopyFileName + fileExtension, copiedFile.FullPath);
            Assert.AreEqual(moveCopyFileName + fileExtension, copiedFile.Name);

            var movedFile = await this.client.Move<Data.File>(moveCopyFile.FullPath, moveCopyToFolderPath);
            Assert.Null(copiedFile.PublicLink);
            Assert.That(copiedFile.FullPath, new StartsWithConstraint(moveCopyToFolderPath + "/" + moveCopyFolderName));
            Assert.That(copiedFile.Name, new StartsWithConstraint(moveCopyFolderName));
        }

        [Test]
        public async Task DownloadMultipleItemsAsZIPTest()
        {
            var tempPath = Path.GetTempPath();

            var directLink = await this.client.GetDirectLinkZIPArchive(new List<string> { TestDownloadFilePath }, null);
            Assert.NotNull(directLink);

            var task = this.client.GetDirectLinkZIPArchive(
                new List<string> { TestDownloadFilePath, TestFolderPath + "/" + Guid.NewGuid() + "/" + Guid.NewGuid() }, null);
            var exception = Assert.ThrowsAsync<CloudClientException>(() => task);
            Assert.AreEqual(ErrorCode.DifferentParentPaths, (ErrorCode)exception.HResult);
            Assert.AreEqual("filesAndFoldersPaths", exception.Source);

            this.prevDownloadProgressPercentage = -1;
            this.client.ProgressChangedEvent += delegate (object sender, ProgressChangedEventArgs e)
            {
                Assert.True(this.prevDownloadProgressPercentage < e.ProgressPercentage, "New progress percentage is equal.");
                this.prevDownloadProgressPercentage = e.ProgressPercentage;
            };

            var archiveName = Guid.NewGuid().ToString();
            var result = await this.client.DownloadItemsAsZIPArchive(new List<string> { TestDownloadFilePath }, archiveName, tempPath);
            Assert.True(result.Exists);
            Assert.AreEqual(archiveName + ".zip", result.Name);
            if (result.Exists)
            {
                result.Delete();
            }
        }

        [Test]
        public async Task DownloadFileTest()
        {
            var tempPath = Path.GetTempPath();

            var task = this.client.DownloadFile(
                TestFileName, TestFolderPath + "/" + Guid.NewGuid().ToString() + ".txt", tempPath);
            var exception = Assert.ThrowsAsync<CloudClientException>(() => task);
            Assert.AreEqual(ErrorCode.PathNotExists, (ErrorCode)exception.HResult);
            Assert.AreEqual("sourceFilePath", exception.Source);

            this.client.ProgressChangedEvent += delegate (object sender, ProgressChangedEventArgs e)
            {
                Assert.True(this.prevDownloadProgressPercentage < e.ProgressPercentage, "New progress percentage is equal.");
                this.prevDownloadProgressPercentage = e.ProgressPercentage;
            };

            var result = await this.client.DownloadFile(TestFileName, TestDownloadFilePath, tempPath);
            Assert.True(result.Exists);
            if (result.Exists)
            {
                result.Delete();
            }
        }

        [Test]
        public async Task FoldersTest()
        {
            var newFolderName = Guid.NewGuid().ToString();
            var newSubfoldername = newFolderName + "/subfolder";
            var result = await this.client.CreateFolder(newFolderName);
            Assert.AreEqual(newFolderName, result.FullPath.Split(new[] { '/' }).Last());

            result = await this.client.CreateFolder(newSubfoldername);
            Assert.AreEqual("subfolder", result.FullPath.Split(new[] { '/' }).Last());
            StringAssert.Contains(newFolderName, result.FullPath);

            var folder = await this.client.GetFolder(newFolderName);
            Assert.NotNull(folder);

            await this.client.Remove(newFolderName);

            folder = await this.client.GetFolder(newFolderName);
            Assert.Null(folder);
        }

        [Test]
        public async Task UploadFileToNotExistingFolderTest()
        {
            var task = this.client.UploadFile(null, this.TestFilePath, TestFolderName + Guid.NewGuid());
            var exception = Assert.ThrowsAsync<CloudClientException>(() => task);
            Assert.AreEqual(ErrorCode.PathNotExists, (ErrorCode)exception.HResult);
            Assert.AreEqual("destFolderPath", exception.Source);
        }

        [Test]
        public async Task UploadFileTest()
        {
            var folder = await this.client.GetFolder(TestFolderPath);
            if (folder == null)
            {
                folder = await this.client.CreateFolder(TestFolderPath);
                Assert.NotNull(folder);
            }

            var fileInfo = new FileInfo(this.TestFilePath);
            var result = await this.client.UploadFile(null, fileInfo.FullName, TestFolderPath);
            Assert.AreEqual(fileInfo.Length, result.Size.DefaultValue);
            StringAssert.Contains(Path.GetFileNameWithoutExtension(TestFileName), result.Name);
            var splittedFullPath = result.FullPath.Split(new[] { '/' });
            StringAssert.Contains(Path.GetFileNameWithoutExtension(TestFileName), splittedFullPath.Last());
            Assert.AreEqual(TestFolderName, splittedFullPath[splittedFullPath.Length - 2]);
            Assert.NotNull(result.Hash);
            Assert.True(result.LastModifiedTimeUTC < DateTime.Now.ToUniversalTime());
            Assert.Null(result.PublicLink);

            //// Check the folder content changing event.
            folder = await this.client.GetFolder(TestFolderPath);
            var hasChangedFolderContentAfterUploading = false;
            folder.FolderContentChangedEvent += (s, e) =>
            {
                hasChangedFolderContentAfterUploading = true;
            };

            await folder.UploadFile(this.TestFilePath);
            Assert.True(hasChangedFolderContentAfterUploading);
        }

        [Test]
        public async Task DiskUsageTest()
        {
            var result = await this.account.GetDiskUsage();
            Assert.True(result.Free.DefaultValue > 0);
            Assert.True(result.Total.DefaultValue > 0);
            Assert.True(result.Used.DefaultValue > 0);
            Assert.True(result.Used.DefaultValue < result.Total.DefaultValue && result.Free.DefaultValue < result.Total.DefaultValue);
        }

        [Test]
        public async Task GetItemsTest()
        {
            var result = await this.client.GetFolder(TestFolderPath);
            Assert.True(result.FilesCount > 0);
            Assert.True(result.FoldersCount > 0);
            Assert.AreEqual(TestFolderPath, result.FullPath);
            Assert.AreEqual(TestFolderName, result.Name);
            Assert.True(result.PublicLink == TestFolderPublicLink);
            Assert.True(result.Size.DefaultValue > 0);
            Assert.AreEqual(result.FilesCount, result.Files.Count());
            Assert.AreEqual(result.FoldersCount, result.Folders.Count());
            foreach (var file in result.Files)
            {
                Assert.True(!string.IsNullOrEmpty(file.FullPath));
                Assert.True(!string.IsNullOrEmpty(file.Hash));
                Assert.True(file.LastModifiedTimeUTC > new DateTime(1970, 1, 1));
                Assert.True(!string.IsNullOrEmpty(file.Name));
                Assert.True(file.PublicLink == null || file.PublicLink.StartsWith("https://cloud.mail.ru/public/"));
            }

            foreach (var folder in result.Folders)
            {
                Assert.True(!string.IsNullOrEmpty(folder.FullPath));
                Assert.True(!string.IsNullOrEmpty(folder.Name));
                Assert.True(folder.PublicLink == null || folder.PublicLink.StartsWith("https://cloud.mail.ru/public/"));
            }

            result = result = await this.client.GetFolder(TestFolderPath + "/" + Guid.NewGuid().ToString());
            Assert.Null(result);
        }

        [OneTimeSetUp]
        public async Task CheckAuthorization()
        {
            if (this.account == null)
            {
                this.account = new Account(Login, Password);
                Assert.True(await this.account.Login());

                this.client = new CloudClient(this.account);
            }
        }
    }
}
