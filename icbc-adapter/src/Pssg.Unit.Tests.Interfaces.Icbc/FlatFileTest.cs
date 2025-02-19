

using FileHelpers;
using Microsoft.Extensions.Configuration;
using Rsbc.Dmf.IcbcAdapter;
using Pssg.Interfaces;
using Pssg.Interfaces.FlatFileModels;
using Pssg.Interfaces.Icbc.FlatFileModels;
using Pssg.Interfaces.Icbc.Models;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using System.Net.Http;
using System.Net;
using Grpc.Net.Client;
using Rsbc.Dmf.CaseManagement.Service;

namespace Rsbc.Dmf.IcbcAdapter.Tests
{
    public class FlatFileTest
    {

        IConfiguration Configuration;
        FlatFileUtils flatFileUtils;
        CaseManagement.Service.CaseManager.CaseManagerClient caseManagerClient;

        /// <summary>
        /// Setup the test
        /// </summary>        
        public FlatFileTest()
        {
            Configuration = new ConfigurationBuilder()
                // The following line is the only reason we have a project reference for the icbc adapter in this test project
                // If you were to use this code on a different project simply add user secrets as appropriate to match the environment / secret variables below.
                .AddUserSecrets<Startup>() // Add secrets from the service.
                .AddEnvironmentVariables()
                .Build();
            // create a new case manager client.

            string cmsAdapterURI = Configuration["CMS_ADAPTER_URI"];

            if (!string.IsNullOrEmpty(cmsAdapterURI))
            {
                var httpClientHandler = new HttpClientHandler();
                // Return `true` to allow certificates that are untrusted/invalid                    
                    httpClientHandler.ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                
                var httpClient = new HttpClient(httpClientHandler);
                // set default request version to HTTP 2.  Note that Dotnet Core does not currently respect this setting for all requests.
                httpClient.DefaultRequestVersion = HttpVersion.Version20;

                if (!string.IsNullOrEmpty(Configuration["CMS_ADAPTER_JWT_SECRET"]))
                {
                    var initialChannel = GrpcChannel.ForAddress(cmsAdapterURI, new GrpcChannelOptions { HttpClient = httpClient });

                    var initialClient = new CaseManager.CaseManagerClient(initialChannel);
                    // call the token service to get a token.
                    var tokenRequest = new CaseManagement.Service.TokenRequest
                    {
                        Secret = Configuration["CMS_ADAPTER_JWT_SECRET"]
                    };

                    var tokenReply = initialClient.GetToken(tokenRequest);

                    if (tokenReply != null && tokenReply.ResultStatus == CaseManagement.Service.ResultStatus.Success)
                    {
                        // Add the bearer token to the client.
                        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {tokenReply.Token}");
                    }
                }

                var channel = GrpcChannel.ForAddress(cmsAdapterURI, new GrpcChannelOptions { HttpClient = httpClient });
                caseManagerClient = new CaseManager.CaseManagerClient(channel);
            }
             
            flatFileUtils = new FlatFileUtils(Configuration,caseManagerClient);
        }


        [Fact]
        public async void BasicConnectionTest()
        {
            flatFileUtils.CheckConnection(null);
        }

        [Fact]
        public async void TestCheckForCandidates()
        {
            flatFileUtils.CheckForCandidates(null);
        }

        [Fact]
        public async void TestAddCandidate()
        {
            var IcbcClient = new IcbcClient(Configuration);
            CLNT client = IcbcClient.GetDriverHistory(Configuration["ICBC_TEST_DL"]);

            LegacyCandidateRequest lcr = new LegacyCandidateRequest()
            {
                LicenseNumber = Configuration["ICBC_TEST_DL"],
                Surname = client.INAM.SURN
            };
            caseManagerClient.ProcessLegacyCandidate(lcr);
        }

        [Fact]
        public async void TestSendUpdates()
        {
            flatFileUtils.SendMedicalUpdates(null);
        }

        [Fact]
        public async void CheckDriverNewFileFormat()
        {
            var engine = new FileHelperEngine<NewDriver>();
            string sampleData = "2222222022222224EXPERIMENTAL_______________________2012-01-011M2002-02-012004-01-011998-01-012002-04-040100";
            var records = engine.ReadString(sampleData);
            Assert.Equal(records[0].LicenseNumber, sampleData.Substring(0,7));
        }

        [Fact]
        public async void CheckMedicalUpdateFormat()
        {
            var engine = new FileHelperEngine<MedicalUpdate>();
            string sampleData = "2222222EXPERIMENTAL_______________________P2012-01-01";
            var records = engine.ReadString(sampleData);
            Assert.Equal(records[0].LicenseNumber, sampleData.Substring(0, 7));
        }
        
        [Fact]
        public async void CandidateListTest()
        {
            LegacyCandidateRequest lcr = new LegacyCandidateRequest()
            {
                LicenseNumber = Configuration["ICBC_TEST_DL"],
                Surname = Configuration["ICBC_TEST_SURNAME"],
                ClientNumber = String.Empty,
            };
            var result = caseManagerClient.ProcessLegacyCandidate(lcr);
            Assert.NotNull(result);            
        }

        [Fact]
        public async void MedicalStatusPass()
        {
            // create a FlatFilesUtil class.
            var f = new FlatFileUtils(Configuration, caseManagerClient);

            SearchReply searchReply = new SearchReply();
            var testCase = new DmerCase() { Driver = new Driver() { Surname = "TEST" } };
            testCase.Decisions.Add(
                new DecisionItem () { CreatedOn = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), Identifier = Guid.NewGuid().ToString(), Outcome = DecisionItem.Types.DecisionOutcomeOptions.FitToDrive  });
            searchReply.Items.Add(testCase);
            var medicalUpdateData = f.GetMedicalUpdateData(searchReply);
            // should be P for Pass
            Assert.Equal("P", medicalUpdateData[0].MedicalDisposition);
        }

        [Fact]
        public async void MedicalStatusFail()
        {
            // create a FlatFilesUtil class.
            var f = new FlatFileUtils(Configuration, caseManagerClient);

            SearchReply searchReply = new SearchReply();
            var testCase = new DmerCase() { Driver = new Driver() { Surname = "TEST" } };
            testCase.Decisions.Add(
                new DecisionItem() { CreatedOn = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), Identifier = Guid.NewGuid().ToString(), Outcome = DecisionItem.Types.DecisionOutcomeOptions.UnfitToDrive });
            searchReply.Items.Add(testCase);
            var medicalUpdateData = f.GetMedicalUpdateData(searchReply);
            // should be J for Adjudication
            Assert.Equal("J", medicalUpdateData[0].MedicalDisposition);
        }

        [Fact]
        public async void MedicalStatusFailPass()
        {
            // create a FlatFilesUtil class.
            var f = new FlatFileUtils(Configuration, caseManagerClient);

            SearchReply searchReply = new SearchReply();

            var testCase = new DmerCase() { Driver = new Driver() { Surname = "TEST" } };

            testCase.Decisions.Add(
                new DecisionItem() { CreatedOn = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-1)), Identifier = Guid.NewGuid().ToString(), Outcome = DecisionItem.Types.DecisionOutcomeOptions.UnfitToDrive });

            testCase.Decisions.Add(
                new DecisionItem() { CreatedOn = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), Identifier = Guid.NewGuid().ToString(), Outcome = DecisionItem.Types.DecisionOutcomeOptions.FitToDrive });
            searchReply.Items.Add(testCase);
            var medicalUpdateData = f.GetMedicalUpdateData(searchReply);
            // should be P for Pass, as the Pass Decision is after the Fail.
            Assert.Equal("P", medicalUpdateData[0].MedicalDisposition);
        }

    }
}
