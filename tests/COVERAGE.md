# Harness coverage

The old script harnesses in `tests-scratch/` (`apptest/apptest.cs`, 228 checks, and `apitest/apitest.cs`, 140 checks) have been replaced by the three xUnit projects in this folder. The tables below map every harness check, in the order it printed in `run.log`, to the test or tests that replace it. All 368 checks are ported; none were dropped.

## Running the tests

```bash
dotnet test ChatHub.slnx
```

Docker must be running: each integration project starts its own `postgres:17` container with Testcontainers, applies the real EF Core migrations, and resets the data with Respawn before every test. Uploaded files go to a per-run folder under the system temp directory, which is deleted at the end of the run. Nothing depends on a local database or a fixed path.

The existing suites run without Redis (HybridCache is L1-only). `RedisCacheTests` and `RedisOutageTests` each get their own collection and fixture (`RedisApiFixture`: a `postgres:17` and a `redis:8-alpine` container), so stopping Redis in the outage test cannot affect any other test. `ApiFixture.ResetDatabaseAsync` also clears the whole cache, because Respawn deletes rows behind EF's back and the invalidation interceptor never sees those deletes.

To run one project or one class:

```bash
dotnet test tests/ChatHub.Domain.UnitTests
dotnet test ChatHub.slnx --filter "FullyQualifiedName~Api.IntegrationTests.RealTimeTests"
```

## Test counts

| Project | Test methods | Test cases (theory rows expanded) |
|---|---:|---:|
| ChatHub.Domain.UnitTests | 154 | 189 |
| ChatHub.Application.IntegrationTests | 231 | 240 |
| ChatHub.Api.IntegrationTests | 141 | 144 |
| **Total** | **526** | **573** |

## What changed in the port

- **Login timing (apptest #10).** The stopwatch comparison is gone. The test now asserts that an unknown user and a wrong password both run exactly one `Verify`, against hashes with the same format, PRF, iteration count and salt size. It can't be flaky.
- **Windows-only ACL test (apitest #105, #106, #139).** These use `[WindowsOnlyFact]`, which sets `Skip` on other operating systems. On Windows they run `icacls` against the per-run storage folder and always remove the deny rule again.
- **Client addresses for rate limiting (apitest #22, #29–#33).** TestServer has no sockets, so a test-only `IStartupFilter` (registered with `ConfigureTestServices`; production code is unchanged) sets `RemoteIpAddress` from an `X-Test-Client-Address` header. Every test client gets its own address, so the per-IP limiter never couples tests.
- **Waiting instead of sleeping (apitest #77).** A sentinel message replaces the 1-second sleep, as described in the table.
- **Clock.** Application tests use `FakeTimeProvider` (one per test). A small subclass adds the per-async-flow hook that the race tests need to line up two requests at their first clock read, exactly as the harness did.
- **"No unexpected errors" (apptest #228, apitest #140).** These were end-of-run checks. They now run after every test, in `AppTestBase.DisposeAsync` and `ApiTestBase.DisposeAsync`, so a stray error fails the test that caused it. Tests that expect an error take it explicitly.
- **Migration checks (apitest #1–#4)** live in `Application.IntegrationTests/PersistenceTests`, next to the other schema tests. The backfill test migrates its own throwaway database on the shared container.
- **Pure domain checks (apptest #175, #176, #191, #192)** moved to `Domain.UnitTests`. Checks that combined several rules were split into one test per rule.

## Domain invariants added beyond the harnesses

`ChatHub.Domain.UnitTests` has 146 test methods that cover Domain rules the harnesses never exercised:

| Class | Added test methods |
|---|---:|
| `AttachmentTests` | 3 |
| `BaseEntityTests` | 2 |
| `ConversationMemberTests` | 24 |
| `ConversationTests` | 46 |
| `EmailTests` | 5 |
| `MessageTests` | 20 |
| `PhoneNumberTests` | 6 |
| `RefreshTokenTests` | 8 |
| `UploadedFileTests` | 7 |
| `UserBlockTests` | 3 |
| `UserProfileTests` | 22 |

## apptest.cs (228 checks)

| # | Harness check | Replaced by | Notes |
|---|---|---|---|
| 1 | register returns token pair | Application.IntegrationTests:<br>`AuthTests.Register_ValidRequest_ReturnsTokenPair` |  |
| 2 | duplicate email -> Auth.ContactInUse | Application.IntegrationTests:<br>`AuthTests.Register_DuplicateEmailInAnotherCase_ReturnsContactInUse` |  |
| 3 | duplicate phone -> Auth.ContactInUse | Application.IntegrationTests:<br>`AuthTests.Register_DuplicatePhoneInAnotherFormat_ReturnsContactInUse` |  |
| 4 | register validation: contact/password/name -> DisplayName,Contact,Password | Application.IntegrationTests:<br>`AuthTests.Register_MissingNameContactAndShortPassword_ThrowsValidationException` |  |
| 5 | register validation: bad email/phone, no digit | Application.IntegrationTests:<br>`AuthTests.Register_InvalidEmailPhoneAndPasswordWithoutDigit_ThrowsValidationException` |  |
| 6 | login by email (case-insensitive) | Application.IntegrationTests:<br>`AuthTests.Login_EmailInDifferentCase_Succeeds` |  |
| 7 | login by formatted phone | Application.IntegrationTests:<br>`AuthTests.Login_FormattedPhoneNumber_SignsInThatUser` |  |
| 8 | [4] unknown user: Verify runs against the dummy hash (computed on first use) | Application.IntegrationTests:<br>`AuthTests.Login_UnknownUser_VerifiesPasswordAgainstDummyHashComputedOnce` | "Computed on first use" is asserted order-independently: the verified hash was produced by `IPasswordHasher.Hash` exactly once in the test process and belongs to no user. |
| 9 | [4] unparseable identifier also verifies; dummy hash is not computed again | Application.IntegrationTests:<br>`AuthTests.Login_UnparseableIdentifier_VerifiesAgainstSameDummyHashWithoutRehashing` |  |
| 10 | [4] timing: unknown user vs wrong password (ms) | Application.IntegrationTests:<br>`AuthTests.Login_UnknownUserAndWrongPassword_DoTheSamePasswordHashingWork` | Rewritten without a stopwatch: both paths run exactly one `Verify`, against hashes with the same format, PRF, iteration count and salt size. |
| 11 | same error for wrong pw / unknown / garbage | Application.IntegrationTests:<br>`AuthTests.Login_InvalidCredentials_ReturnsTheSameError` | Theory, 3 cases |
| 12 | refresh rotates | Application.IntegrationTests:<br>`AuthTests.Refresh_ValidToken_RotatesTheRefreshToken` |  |
| 13 | reusing rotated token -> Auth.TokenReused | Application.IntegrationTests:<br>`AuthTests.Refresh_AlreadyRotatedToken_ReturnsTokenReused` |  |
| 14 | token issued before reuse is revoked too | Application.IntegrationTests:<br>`AuthTests.Refresh_AfterReuseDetected_TokenIssuedByTheRotationIsRevoked` |  |
| 15 | all of alice's sessions revoked after reuse | Application.IntegrationTests:<br>`AuthTests.Refresh_AfterReuseDetected_AllOtherSessionsAreRevoked` |  |
| 16 | unknown refresh token -> Auth.InvalidRefreshToken | Application.IntegrationTests:<br>`AuthTests.Refresh_UnknownToken_ReturnsInvalidRefreshToken` |  |
| 17 | [1] RowVersion: shadow uint concurrency token mapped to xmin | Application.IntegrationTests:<br>`PersistenceTests.RefreshTokenModel_RowVersion_IsShadowUintConcurrencyTokenMappedToXmin` |  |
| 18 | [1] xmin is a system column: the schema does not create it | Application.IntegrationTests:<br>`PersistenceTests.CreateScript_RefreshTokenRowVersion_DoesNotCreateXminColumn` |  |
| 19 | [1] two concurrent refreshes, same token: exactly one succeeds, other -> Auth.InvalidRefreshToken | Application.IntegrationTests:<br>`AuthConcurrencyTests.Refresh_ConcurrentWithTheSameToken_ExactlyOneSucceeds` | Racers are lined up with a barrier on the `FakeTimeProvider` subclass, like the harness. |
| 20 | [1] loser's pair rolled back; original token replaced by the winner's | Application.IntegrationTests:<br>`AuthConcurrencyTests.Refresh_ConcurrentWithTheSameToken_LoserPairIsRolledBack` |  |
| 21 | [1] no sweep: the winner's session still refreshes | Application.IntegrationTests:<br>`AuthConcurrencyTests.Refresh_ConcurrentWithTheSameToken_WinnerSessionStillRefreshes` |  |
| 22 | [1] reuse sweep with a rotation landing mid-sweep: sweep retries instead of failing | Application.IntegrationTests:<br>`AuthConcurrencyTests.Refresh_ReuseSweepRacingARotation_RetriesAndReportsReuse` |  |
| 23 | [1] the token issued mid-sweep was revoked too | Application.IntegrationTests:<br>`AuthConcurrencyTests.Refresh_ReuseSweepRacingARotation_RevokesTheTokenIssuedMidSweep` |  |
| 24 | [3] concurrent register, same email: one succeeds, other -> Auth.ContactInUse | Application.IntegrationTests:<br>`AuthConcurrencyTests.Register_ConcurrentWithTheSameEmail_OneSucceedsAndTheOtherGetsContactInUse` |  |
| 25 | [3] concurrent register, same phone: one succeeds, other -> Auth.ContactInUse | Application.IntegrationTests:<br>`AuthConcurrencyTests.Register_ConcurrentWithTheSamePhone_OneSucceedsAndTheOtherGetsContactInUse` |  |
| 26 | [3] only one user stored per contact | Application.IntegrationTests:<br>`AuthConcurrencyTests.Register_ConcurrentWithTheSameContact_StoresOnlyOneUserPerContact` |  |
| 27 | logout idempotent | Application.IntegrationTests:<br>`AuthTests.Logout_CalledTwiceWithTheSameToken_SucceedsBothTimes` |  |
| 28 | change password: wrong current | Application.IntegrationTests:<br>`AuthTests.ChangePassword_WrongCurrentPassword_ReturnsInvalidCurrentPassword` |  |
| 29 | change password ok | Application.IntegrationTests:<br>`AuthTests.ChangePassword_CorrectCurrentPassword_Succeeds` |  |
| 30 | change password revokes old sessions | Application.IntegrationTests:<br>`AuthTests.ChangePassword_Success_RevokesExistingSessions` |  |
| 31 | new pair from change password works | Application.IntegrationTests:<br>`AuthTests.ChangePassword_Success_ReturnsAWorkingTokenPair` |  |
| 32 | login uses new password | Application.IntegrationTests:<br>`AuthTests.ChangePassword_Success_LoginRequiresTheNewPassword` |  |
| 33 | GetMe value objects | Application.IntegrationTests:<br>`UserTests.GetMe_PhoneOnlyUser_ReturnsNormalizedPhoneAndNoEmail` |  |
| 34 | update profile | Application.IntegrationTests:<br>`UserTests.UpdateMyProfile_ValidRequestWithAvatar_Succeeds` |  |
| 35 | profile updated + email lowercase | Application.IntegrationTests:<br>`UserTests.UpdateMyProfile_ValidRequest_PersistsTrimmedValuesAndKeepsLowercaseEmail` |  |
| 36 | avatar must be an Avatar file (was: javascript: avatar rejected) | Application.IntegrationTests:<br>`UserTests.UpdateMyProfile_MessageAttachmentFileAsAvatar_ReturnsWrongPurpose` |  |
| 37 | block idempotent | Application.IntegrationTests:<br>`UserTests.BlockUser_CalledTwice_SucceedsBothTimes` |  |
| 38 | cannot block self | Application.IntegrationTests:<br>`UserTests.BlockUser_Self_ReturnsCannotBlockSelf` |  |
| 39 | block unknown -> NotFound | Application.IntegrationTests:<br>`UserTests.BlockUser_UnknownUser_ReturnsNotFound` |  |
| 40 | search by name substring | Application.IntegrationTests:<br>`UserTests.SearchUsers_NameSubstring_ReturnsMatchingUser` |  |
| 41 | search hides blocker (dave can't find carol) | Application.IntegrationTests:<br>`UserTests.SearchUsers_ByUserWhoWasBlocked_HidesTheBlocker` |  |
| 42 | search hides blocked (carol can't find dave) | Application.IntegrationTests:<br>`UserTests.SearchUsers_ByBlocker_HidesTheBlockedUser` |  |
| 43 | search by exact email | Application.IntegrationTests:<br>`UserTests.SearchUsers_ExactEmailInAnyCase_ReturnsThatUser` |  |
| 44 | search by exact phone | Application.IntegrationTests:<br>`UserTests.SearchUsers_ExactPhoneNumber_ReturnsThatUser` |  |
| 45 | no partial email matches | Application.IntegrationTests:<br>`UserTests.SearchUsers_PartialEmail_ReturnsNothing` |  |
| 46 | search needs 2+ chars | Application.IntegrationTests:<br>`UserTests.SearchUsers_SingleCharacterQuery_ThrowsValidationException` |  |
| 47 | search excludes caller | Application.IntegrationTests:<br>`UserTests.SearchUsers_QueryMatchingOwnName_ExcludesTheCaller` |  |
| 48 | presence hidden from blocked user only | Application.IntegrationTests:<br>`UserTests.GetUserProfile_OnlineUserWhoBlockedTheViewer_HidesPresenceFromTheBlockedViewerOnly` |  |
| 49 | IsBlockedByMe | Application.IntegrationTests:<br>`UserTests.GetUserProfile_IsBlockedByMe_ReflectsTheViewersOwnBlocks` |  |
| 50 | GetMyBlockedUsers | Application.IntegrationTests:<br>`UserTests.GetMyBlockedUsers_AfterBlocking_ListsTheBlockedUser` |  |
| 51 | direct: create once, reuse both ways | Application.IntegrationTests:<br>`ConversationTests.CreateDirectConversation_RepeatedFromEitherSide_ReusesTheSameConversation` |  |
| 52 | direct: both users joined group | Application.IntegrationTests:<br>`ConversationTests.CreateDirectConversation_New_AddsBothUsersToTheSignalRGroup` |  |
| 53 | direct with blocker -> Forbidden | Application.IntegrationTests:<br>`ConversationTests.CreateDirectConversation_WithUserWhoBlockedTheCaller_ReturnsBlocked` |  |
| 54 | direct with self | Application.IntegrationTests:<br>`ConversationTests.CreateDirectConversation_WithSelf_ReturnsCannotChatWithSelf` |  |
| 55 | group: owner + existing users, deduped, unknown skipped | Application.IntegrationTests:<br>`ConversationTests.CreateGroup_MemberListWithDuplicatesUnknownAndOwner_KeepsOwnerAndExistingUsersOnce` |  |
| 56 | GetMyConversations runs one SQL command | Application.IntegrationTests:<br>`ConversationListTests.GetMyConversations_AnyPage_RunsOneSqlCommand` |  |
| 57 | page 1 = most recent (direct) | Application.IntegrationTests:<br>`ConversationListTests.GetMyConversations_FirstPage_ReturnsTheMostRecentConversationAndACursor` |  |
| 58 | direct title/image from other user | Application.IntegrationTests:<br>`ConversationListTests.GetMyConversations_Direct_TakesTitleAndImageFromTheOtherUser` |  |
| 59 | direct unread + preview | Application.IntegrationTests:<br>`ConversationListTests.GetMyConversations_Direct_ReturnsUnreadCountAndLastMessagePreview` |  |
| 60 | page 2 via cursor = group, no more | Application.IntegrationTests:<br>`ConversationListTests.GetMyConversations_SecondPageViaCursor_ReturnsTheOlderConversationAndNoMore` |  |
| 61 | group: unread excludes own msgs, preview truncated | Application.IntegrationTests:<br>`ConversationListTests.GetMyConversations_Group_UnreadExcludesOwnMessagesAndPreviewIsTruncated` |  |
| 62 | alice unread in group = 3 | Application.IntegrationTests:<br>`ConversationListTests.GetMyConversations_Group_CountsEveryMessageFromOthersAsUnread` |  |
| 63 | deleted preview + not counted | Application.IntegrationTests:<br>`ConversationListTests.GetMyConversations_LastMessageDeleted_ShowsDeletedPreviewAndStopsCountingIt` |  |
| 64 | details: role + members (owner first) | Application.IntegrationTests:<br>`ConversationTests.GetConversationDetails_GroupOwner_ReturnsMyRoleAndMembersOwnerFirst` |  |
| 65 | direct details title | Application.IntegrationTests:<br>`ConversationTests.GetConversationDetails_Direct_TakesTitleAndOtherUserFromTheOtherMember` |  |
| 66 | details: non-member -> Forbidden | Application.IntegrationTests:<br>`ConversationTests.GetConversationDetails_NonMember_ReturnsNotMember` |  |
| 67 | details: unknown -> NotFound | Application.IntegrationTests:<br>`ConversationTests.GetConversationDetails_UnknownConversation_ReturnsNotFound` |  |
| 68 | update group info | Application.IntegrationTests:<br>`ConversationTests.UpdateGroupInfo_ByOwner_Succeeds` |  |
| 69 | member can't update group (domain 422) | Application.IntegrationTests:<br>`ConversationTests.UpdateGroupInfo_ByPlainMember_ThrowsDomainRuleViolation` |  |
| 70 | promote bob | Application.IntegrationTests:<br>`ConversationTests.ChangeMemberRole_OwnerPromotesMemberToAdmin_Succeeds` |  |
| 71 | admin kicks dave | Application.IntegrationTests:<br>`ConversationTests.KickMember_AdminKicksPlainMember_Succeeds` |  |
| 72 | kicked user removed from group | Application.IntegrationTests:<br>`ConversationTests.KickMember_Success_RemovesTheKickedUserFromTheSignalRGroup` |  |
| 73 | kicked user lost access | Application.IntegrationTests:<br>`ConversationTests.KickMember_Success_KickedUserLosesAccess` |  |
| 74 | re-add kicked member (rejoin) + group join | Application.IntegrationTests:<br>`ConversationTests.AddMember_PreviouslyKickedUser_RejoinsAndJoinsTheSignalRGroup` |  |
| 75 | carol leaves | Application.IntegrationTests:<br>`ConversationTests.LeaveConversation_GroupMember_LeavesAndIsRemovedFromTheSignalRGroup` |  |
| 76 | add member with block either way -> Forbidden | Application.IntegrationTests:<br>`ConversationTests.AddMember_UserBlockedEitherWay_ReturnsBlocked` | Theory, 2 cases (each block direction) |
| 77 | re-add carol | Application.IntegrationTests:<br>`ConversationTests.AddMember_UserWhoLeft_CanBeAddedAgain` |  |
| 78 | leave direct -> domain 422 | Application.IntegrationTests:<br>`ConversationTests.LeaveConversation_Direct_ThrowsDomainRuleViolation` |  |
| 79 | owner can't leave before transfer | Application.IntegrationTests:<br>`ConversationTests.LeaveConversation_OwnerWithOtherMembers_ThrowsDomainRuleViolation` |  |
| 80 | transfer ownership | Application.IntegrationTests:<br>`ConversationTests.TransferOwnership_ToMember_Succeeds` |  |
| 81 | roles after transfer | Application.IntegrationTests:<br>`ConversationTests.TransferOwnership_Success_FormerOwnerBecomesAdminAndNewOwnerIsOwner` |  |
| 82 | role=Owner rejected by validator | Application.IntegrationTests:<br>`ConversationTests.ChangeMemberRole_ToOwner_ThrowsValidationException` |  |
| 83 | mute | Application.IntegrationTests:<br>`ConversationTests.MuteConversation_Member_Succeeds` |  |
| 84 | muted shows in list | Application.IntegrationTests:<br>`ConversationTests.MuteConversation_Success_ShowsAsMutedInTheList` |  |
| 85 | unmute | Application.IntegrationTests:<br>`ConversationTests.UnmuteConversation_MutedMember_Succeeds` |  |
| 86 | clear chat | Application.IntegrationTests:<br>`ConversationTests.ClearChat_Member_Succeeds` |  |
| 87 | after clear: no unread, no preview | Application.IntegrationTests:<br>`ConversationTests.ClearChat_AfterMuteAndUnmute_ResetsUnreadAndPreviewAndStaysUnmuted` |  |
| 88 | mute non-member -> Forbidden | Application.IntegrationTests:<br>`ConversationTests.MuteConversation_NonMember_ReturnsNotMember` |  |
| 89 | mute unknown -> NotFound | Application.IntegrationTests:<br>`ConversationTests.MuteConversation_UnknownConversation_ReturnsNotFound` |  |
| 90 | unblock idempotent | Application.IntegrationTests:<br>`UserTests.UnblockUser_CalledTwice_SucceedsBothTimes` |  |
| 91 | unblock hard-deleted | Application.IntegrationTests:<br>`UserTests.UnblockUser_BlockedUser_RemovesTheBlock` |  |
| 92 | delete account | Application.IntegrationTests:<br>`AccountDeletionTests.DeleteMyAccount_ActiveUser_Succeeds` |  |
| 93 | deleted user cannot log in | Application.IntegrationTests:<br>`AccountDeletionTests.DeleteMyAccount_Success_UserCanNoLongerLogIn` |  |
| 94 | deleted user's tokens revoked | Application.IntegrationTests:<br>`AccountDeletionTests.DeleteMyAccount_Success_RevokesRefreshTokens` |  |
| 95 | email reusable after delete | Application.IntegrationTests:<br>`AccountDeletionTests.DeleteMyAccount_Success_EmailCanBeRegisteredAgain` |  |
| 96 | search excludes deleted user | Application.IntegrationTests:<br>`AccountDeletionTests.DeleteMyAccount_Success_DeletedUserIsExcludedFromSearch` |  |
| 97 | [2] owner deletes account | Application.IntegrationTests:<br>`AccountDeletionTests.DeleteMyAccount_OwnerOfSeveralGroups_Succeeds` |  |
| 98 | [2] owner with members: ownership -> highest role, then earliest join (Max, not Moe or Mia) | Application.IntegrationTests:<br>`AccountDeletionTests.DeleteMyAccount_OwnerWithMembers_TransfersOwnershipToHighestRoleThenEarliestJoin` |  |
| 99 | [2] same role and join time: ownership -> lowest member Id | Application.IntegrationTests:<br>`AccountDeletionTests.DeleteMyAccount_SuccessorsTiedOnRoleAndJoinTime_TransfersToTheLowestMemberId` |  |
| 100 | [2] sole owner just leaves | Application.IntegrationTests:<br>`AccountDeletionTests.DeleteMyAccount_SoleOwner_JustLeavesTheGroup` |  |
| 101 | [2] normal member leaves; group owner unchanged | Application.IntegrationTests:<br>`AccountDeletionTests.DeleteMyAccount_PlainMember_LeavesAndTheGroupOwnerIsUnchanged` |  |
| 102 | [2] direct conversation membership kept | Application.IntegrationTests:<br>`AccountDeletionTests.DeleteMyAccount_DirectConversation_KeepsTheMembership` |  |
| 103 | [2] MemberLeft raised for each group | Application.IntegrationTests:<br>`AccountDeletionTests.DeleteMyAccount_MemberOfFourGroups_RaisesMemberLeftForEachGroup` |  |
| 104 | [2] MemberRoleChanged raised for both sides of each transfer, none for g3/g4 | Application.IntegrationTests:<br>`AccountDeletionTests.DeleteMyAccount_OwnerOfGroups_RaisesRoleChangesForBothSidesOfEachTransferOnly` |  |
| 105 | [2] removed from the 4 SignalR groups after save, not from the direct one | Application.IntegrationTests:<br>`AccountDeletionTests.DeleteMyAccount_MemberOfFourGroups_RemovedFromTheirSignalRGroupsButNotTheDirectOne` |  |
| 106 | [5] GetMyConversations still one SQL command | Application.IntegrationTests:<br>`DeletedAccountVisibilityTests.GetMyConversations_WithDeletedOtherUser_StillRunsOneSqlCommand` |  |
| 107 | [5] conversation list: deleted other user masked (title/image too) | Application.IntegrationTests:<br>`DeletedAccountVisibilityTests.GetMyConversations_DeletedOtherUser_MasksUserTitleAndImage` |  |
| 108 | [5] preview sender name masked | Application.IntegrationTests:<br>`DeletedAccountVisibilityTests.GetMyConversations_LastMessageFromDeletedUser_MasksTheSenderName` |  |
| 109 | [5] details: other user + member entry masked, deleted listed last | Application.IntegrationTests:<br>`DeletedAccountVisibilityTests.GetConversationDetails_DeletedOtherUser_IsMaskedAndListedLast` |  |
| 110 | [5] blocked list: deleted user masked | Application.IntegrationTests:<br>`DeletedAccountVisibilityTests.GetMyBlockedUsers_BlockedUserDeletedTheirAccount_IsMasked` |  |
| 111 | [5] UserSummary.From masks too | Application.IntegrationTests:<br>`UserTests.UserSummaryFrom_DeletedUser_IsMasked` |  |
| 112 | [5] message before join: no preview, not unread | Application.IntegrationTests:<br>`ConversationListTests.GetMyConversations_MessageSentBeforeJoining_HasNoPreviewAndIsNotUnread` |  |
| 113 | [5] message after join: preview + unread | Application.IntegrationTests:<br>`ConversationListTests.GetMyConversations_MessageSentAfterJoining_IsPreviewedAndUnread` |  |
| 114 | [5] rejoin after clear: max(JoinedAt, ClearedAt) = rejoin time hides the message | Application.IntegrationTests:<br>`ConversationListTests.GetMyConversations_RejoinAfterClearAndLeave_HidesMessagesSentWhileAway` |  |
| 115 | [events] CreateDirect: ConversationCreated sent directly to both users, title = the other user's name | Application.IntegrationTests:<br>`RealTimeEventTests.CreateDirectConversation_New_SendsConversationCreatedToEachUserTitledWithTheOtherName` |  |
| 116 | [events] CreateGroup with 2 members: owner and each member get exactly one ConversationCreated (MemberAdded reaches nobody yet) | Application.IntegrationTests:<br>`RealTimeEventTests.CreateGroup_WithTwoMembers_SendsExactlyOneConversationCreatedToEachParticipant` |  |
| 117 | [msg] send text (trimmed), response is the projection | Application.IntegrationTests:<br>`MessageTests.SendMessage_Text_ReturnsTheTrimmedProjection` |  |
| 118 | [events] MessageSent -> MessageReceived to the group only, once per member | Application.IntegrationTests:<br>`RealTimeEventTests.SendMessage_Group_BroadcastsMessageReceivedOncePerMemberViaTheGroup` |  |
| 119 | [msg] send image with caption (attachment from the uploaded file, signed url) | Application.IntegrationTests:<br>`MessageTests.SendMessage_ImageWithCaption_AttachesTheUploadedFileWithASignedUrl` |  |
| 120 | [msg] send voice | Application.IntegrationTests:<br>`MessageTests.SendMessage_Voice_AttachesTheAudioFile` |  |
| 121 | [msg] send file (any content type) | Application.IntegrationTests:<br>`MessageTests.SendMessage_File_AcceptsAnyContentType` |  |
| 122 | [msg] validator: System type rejected | Application.IntegrationTests:<br>`MessageTests.SendMessage_SystemType_ThrowsValidationException` |  |
| 123 | [msg] image needs an image/* file, voice needs an audio/* file | Application.IntegrationTests:<br>`MessageTests.SendMessage_FileTypeNotMatchingTheMessageType_ReturnsTypeMismatch` | Theory, 2 cases |
| 124 | [msg] attachment must be your own, unused file (was: URL checks) | Application.IntegrationTests:<br>`MessageTests.SendMessage_FileNotOwnedOrAlreadyUsed_IsRejected` |  |
| 125 | [msg] validator: text needs content and no attachment; image needs an attachment | Application.IntegrationTests:<br>`MessageTests.SendMessage_ContentAndAttachmentNotMatchingTheType_ThrowsValidationException` | Theory, 3 cases |
| 126 | [msg] empty upload rejected (was: file name/size validator) | Application.IntegrationTests:<br>`FileTests.UploadFile_EmptyFile_ThrowsValidationException` |  |
| 127 | [msg] blocked direct chat -> Forbidden | Application.IntegrationTests:<br>`MessageTests.SendMessage_DirectChatWithABlock_ReturnsBlocked` |  |
| 128 | [msg] reply to a message from another conversation -> NotFound | Application.IntegrationTests:<br>`MessageTests.SendMessage_ReplyToAMessageFromAnotherConversation_ReturnsReplyTargetNotFound` |  |
| 129 | [msg] non-member send -> Forbidden | Application.IntegrationTests:<br>`MessageTests.SendMessage_NonMember_ReturnsNotMember` |  |
| 130 | [msg] reply to a message before the caller cleared -> NotFound | Application.IntegrationTests:<br>`MessageTests.SendMessage_ReplyToAMessageTheCallerCleared_ReturnsReplyTargetNotFound` |  |
| 131 | [msg] reply carries a preview of the parent | Application.IntegrationTests:<br>`MessageTests.SendMessage_Reply_CarriesAPreviewOfTheParent` |  |
| 132 | add tess to crew | Application.IntegrationTests:<br>`ConversationTests.AddMember_NewUserToGroup_Succeeds` |  |
| 133 | [events] AddMember: MemberAdded {conversationId, member, role} to the existing members via the group | Application.IntegrationTests:<br>`RealTimeEventTests.AddMember_ToGroup_SendsMemberAddedToTheExistingMembersViaTheGroup` |  |
| 134 | [events] AddMember: ConversationCreated sent directly to the added user only (no pre-join preview) | Application.IntegrationTests:<br>`RealTimeEventTests.AddMember_ToGroup_SendsConversationCreatedOnlyToTheAddedUserWithoutPreJoinPreview` |  |
| 135 | [msg] reply to a message before the caller joined -> NotFound | Application.IntegrationTests:<br>`MessageTests.SendMessage_ReplyToAMessageFromBeforeTheCallerJoined_ReturnsReplyTargetNotFound` |  |
| 136 | [msg] reply: sender sees the parent preview; the broadcast hides it because members joined/cleared after the parent | Application.IntegrationTests:<br>`RealTimeEventTests.SendMessage_ReplyWhenSomeMembersCannotSeeTheParent_SenderSeesThePreviewButTheBroadcastHidesIt` |  |
| 137 | [msg] GetMessages runs one SQL command | Application.IntegrationTests:<br>`MessageTests.GetMessages_AnyPage_RunsOneSqlCommand` |  |
| 138 | [msg] GetMessages newest-first, cursor page 2 continues without overlap | Application.IntegrationTests:<br>`MessageTests.GetMessages_SecondPageViaCursor_ContinuesNewestFirstWithoutOverlap` |  |
| 139 | [msg] all pages: 7 messages, newest first, last page has no cursor | Application.IntegrationTests:<br>`MessageTests.GetMessages_AllPages_ReturnEveryMessageNewestFirstAndEndWithoutACursor` |  |
| 140 | [msg] GetMessages hides pre-clear messages (and the cleared parent's text in replies) | Application.IntegrationTests:<br>`MessageTests.GetMessages_AfterClear_HidesEarlierMessagesAndTheirTextInReplies` |  |
| 141 | [msg] GetMessages hides pre-join messages | Application.IntegrationTests:<br>`MessageTests.GetMessages_AfterJoining_HidesEarlierMessages` |  |
| 142 | [msg] reply text visible to a member who saw the parent | Application.IntegrationTests:<br>`MessageTests.GetMessages_ReplyToAParentTheViewerSaw_ShowsTheParentText` |  |
| 143 | [msg] GetMessages on an empty conversation | Application.IntegrationTests:<br>`MessageTests.GetMessages_EmptyConversation_ReturnsAnEmptyLastPage` |  |
| 144 | [msg] GetMessages: non-member -> Forbidden, unknown -> NotFound | Application.IntegrationTests:<br>`MessageTests.GetMessages_NonMemberOrUnknownConversation_ReturnsNotMemberOrNotFound` |  |
| 145 | [msg] edit by non-sender -> domain 422 | Application.IntegrationTests:<br>`MessageTests.EditMessage_ByNonSender_ThrowsDomainRuleViolation` |  |
| 146 | [msg] delete by non-sender -> domain 422 | Application.IntegrationTests:<br>`MessageTests.DeleteMessage_ByNonSender_ThrowsDomainRuleViolation` |  |
| 147 | [msg] edit in a conversation you are not in -> Forbidden | Application.IntegrationTests:<br>`MessageTests.EditMessage_InAConversationTheCallerIsNotIn_ReturnsNotMember` |  |
| 148 | [msg] edit unknown message -> NotFound | Application.IntegrationTests:<br>`MessageTests.EditMessage_UnknownMessage_ReturnsNotFound` |  |
| 149 | [msg] edit by sender returns the updated message | Application.IntegrationTests:<br>`MessageTests.EditMessage_BySender_ReturnsTheUpdatedMessage` |  |
| 150 | [events] MessageEdited -> MessageUpdated to the group | Application.IntegrationTests:<br>`RealTimeEventTests.EditMessage_Success_BroadcastsMessageUpdatedToTheGroup` |  |
| 151 | [msg] delete by sender | Application.IntegrationTests:<br>`MessageTests.DeleteMessage_BySender_Succeeds` |  |
| 152 | [events] MessageDeleted -> {conversationId, messageId} to the group | Application.IntegrationTests:<br>`RealTimeEventTests.DeleteMessage_Success_BroadcastsConversationAndMessageIdsToTheGroup` |  |
| 153 | [msg] edit a deleted message -> domain 422 | Application.IntegrationTests:<br>`MessageTests.EditMessage_DeletedMessage_ThrowsDomainRuleViolation` |  |
| 154 | [msg] GetMessages masks deleted messages | Application.IntegrationTests:<br>`MessageTests.GetMessages_DeletedMessages_AreMasked` |  |
| 155 | [msg] mark newest as read | Application.IntegrationTests:<br>`ReadStateTests.MarkAsRead_NewestMessage_Succeeds` |  |
| 156 | [events] MarkAsRead newer -> MessagesRead + MessagesDelivered (pointer advanced) to the group | Application.IntegrationTests:<br>`ReadStateTests.MarkAsRead_NewerThanBothPointers_BroadcastsMessagesReadAndMessagesDelivered` |  |
| 157 | [msg] MarkAsRead older message -> success, no event | Application.IntegrationTests:<br>`ReadStateTests.MarkAsRead_OlderThanTheCurrentPointer_SucceedsWithoutEvents` |  |
| 158 | [events] MarkAsDelivered -> MessagesDelivered only | Application.IntegrationTests:<br>`ReadStateTests.MarkAsDelivered_NewerMessage_BroadcastsMessagesDeliveredOnly` |  |
| 159 | [events] MarkAsRead of an already delivered message -> MessagesRead only | Application.IntegrationTests:<br>`ReadStateTests.MarkAsRead_AlreadyDeliveredMessage_BroadcastsMessagesReadOnly` |  |
| 160 | [msg] mark hidden or foreign message -> NotFound | Application.IntegrationTests:<br>`ReadStateTests.MarkMessage_HiddenOrFromAnotherConversation_ReturnsNotFound` |  |
| 161 | [msg] GetReadState lists every active member's pointers | Application.IntegrationTests:<br>`ReadStateTests.GetReadState_Group_ListsEveryActiveMembersPointers` |  |
| 162 | [msg] GetReadState: non-member -> Forbidden | Application.IntegrationTests:<br>`ReadStateTests.GetReadState_NonMember_ReturnsNotMember` |  |
| 163 | [events] MemberRoleChanged to the group | Application.IntegrationTests:<br>`RealTimeEventTests.ChangeMemberRole_Success_BroadcastsMemberRoleChangedToTheGroup` |  |
| 164 | [events] GroupInfoUpdated -> ConversationUpdated to the group | Application.IntegrationTests:<br>`RealTimeEventTests.UpdateGroupInfo_Success_BroadcastsConversationUpdatedToTheGroup` |  |
| 165 | [events] presence: each co-member exactly once (Quinn shares 2 chats); Sam (blocked Paula), Uma (blocked by Paula) and Paula excluded | Application.IntegrationTests:<br>`RealTimeEventTests.GoOnline_SharedConversations_NotifiesEachUnblockedPeerExactlyOnce` |  |
| 166 | [events] presence payload {userId, isOnline, lastSeenUtc} | Application.IntegrationTests:<br>`RealTimeEventTests.GoOnline_Success_SendsTheUserIdOnlineFlagAndLastSeen` |  |
| 167 | [events] ProfileUpdated -> UserSummary to the same peer set | Application.IntegrationTests:<br>`RealTimeEventTests.UpdateMyProfile_Success_SendsTheUserSummaryToTheSamePeers` |  |
| 168 | [events] kick: exactly one MemberRemoved {reason: Kicked} per group member incl. the kicked user, nothing direct | Application.IntegrationTests:<br>`RealTimeEventTests.KickMember_Success_SendsMemberRemovedKickedOncePerMemberIncludingTheKickedUser` |  |
| 169 | [events] leave: exactly one MemberRemoved {reason: Left} per group member incl. the leaver, nothing direct | Application.IntegrationTests:<br>`RealTimeEventTests.LeaveConversation_Success_SendsMemberRemovedLeftOncePerMemberIncludingTheLeaver` |  |
| 170 | [msg] GetMessages masks a deleted sender (message and reply preview) | Application.IntegrationTests:<br>`MessageTests.GetMessages_SenderDeletedTheirAccount_MasksTheSenderAndReplyPreviews` |  |
| 171 | [events] a throwing handler does not fail the command; the error is logged | Application.IntegrationTests:<br>`RealTimeEventTests.SendMessage_HandlerThrows_CommandSucceedsAndTheErrorIsLogged` |  |
| 172 | [events] ...and the message is saved | Application.IntegrationTests:<br>`RealTimeEventTests.SendMessage_HandlerThrows_TheMessageIsStillSaved` |  |
| 173 | [sec1] SecurityStamp set on Create, unique per user | Application.IntegrationTests:<br>`SecurityTests.Register_NewUsers_EachGetsADistinctSecurityStamp` |  |
| 174 | [sec1] ChangePassword rotates the stamp | Application.IntegrationTests:<br>`SecurityTests.ChangePassword_Success_RotatesTheSecurityStamp` |  |
| 175 | [sec1] RotateSecurityStamp() issues a new stamp | Domain.UnitTests:<br>`UserProfileTests.RotateSecurityStamp_Always_IssuesANewStamp` | Pure domain rule, moved to the unit tests. |
| 176 | [sec2] domain: MarkAsRead/MarkAsDelivered(own message) does not throw, moves no pointer, raises no event | Domain.UnitTests:<br>`ConversationMemberTests.MarkAsRead_OwnMessage_MovesNoPointerAndRaisesNoEvent`<br>`ConversationMemberTests.MarkAsDelivered_OwnMessage_MovesNoPointerAndRaisesNoEvent` | Split into one test per method. |
| 177 | [sec9] marking your own message as read/delivered -> success | Application.IntegrationTests:<br>`ReadStateTests.MarkOwnMessage_ReadAndDelivered_Succeeds` |  |
| 178 | [sec9] ...no pointer change and no MessagesRead/MessagesDelivered event | Application.IntegrationTests:<br>`ReadStateTests.MarkOwnMessage_ReadAndDelivered_LeavesPointersAndRaisesNoEvent` |  |
| 179 | [sec9] the other user's earlier message stays unread | Application.IntegrationTests:<br>`ReadStateTests.MarkOwnMessage_AsRead_KeepsTheOtherUsersEarlierMessageUnread` |  |
| 180 | [sec9] marking the other user's message still works | Application.IntegrationTests:<br>`ReadStateTests.MarkAsRead_OtherUsersMessage_ClearsUnreadAndNotifiesBothMembers` |  |
| 181 | [sec7] two validators on one request: each failure reported exactly once | Application.IntegrationTests:<br>`ValidationPipelineTests.Send_TwoValidatorsFailingOnTheSameProperty_ReportsEachFailureExactlyOnce` | The class registers the extra validator through the per-test DI hook. |
| 182 | [sec8] GetMessage: member -> MessageResponse | Application.IntegrationTests:<br>`MessageTests.GetMessage_Member_ReturnsTheMessage` |  |
| 183 | [sec8] GetMessage: non-member -> Forbidden | Application.IntegrationTests:<br>`MessageTests.GetMessage_NonMember_ReturnsNotMember` |  |
| 184 | [sec8] GetMessage: unknown -> NotFound | Application.IntegrationTests:<br>`MessageTests.GetMessage_UnknownMessage_ReturnsNotFound` |  |
| 185 | [sec8] GetMessage: hidden by clear -> NotFound for the member who cleared only | Application.IntegrationTests:<br>`MessageTests.GetMessage_HiddenByClear_ReturnsNotFoundOnlyForTheMemberWhoCleared` |  |
| 186 | [sec8] GetMessage: pre-join message hidden, later one visible | Application.IntegrationTests:<br>`MessageTests.GetMessage_SentBeforeAndAfterTheCallerJoined_OnlyTheLaterOneIsVisible` |  |
| 187 | [sec8] GetMessage: deleted message returned masked | Application.IntegrationTests:<br>`MessageTests.GetMessage_DeletedMessage_ReturnsItMasked` |  |
| 188 | [sec6] hub: active user is tracked and joins their 2 conversations | Application.IntegrationTests:<br>`ChatHubTests.OnConnected_ActiveUser_IsTrackedAndJoinsEachConversationGroup` |  |
| 189 | [sec1] Delete rotates the stamp | Application.IntegrationTests:<br>`SecurityTests.DeleteMyAccount_Success_RotatesTheSecurityStamp` |  |
| 190 | [sec6] hub: deleted user's connection is aborted, not tracked, joins nothing | Application.IntegrationTests:<br>`ChatHubTests.OnConnected_DeletedUser_IsAbortedNotTrackedAndJoinsNothing` |  |
| 191 | [file] domain: avatar must be image/*, MarkAttached only once | Domain.UnitTests:<br>`UploadedFileTests.Create_AvatarOrGroupImageThatIsNotAnImage_Throws`<br>`UploadedFileTests.MarkAttached_SecondTime_Throws`<br>`UploadedFileTests.Create_MixedCaseContentType_IsStoredLowercaseWithTrimmedValues` | Split into its three rules. |
| 192 | [file] domain: MarkDeleted is idempotent, a deleted file cannot be attached | Domain.UnitTests:<br>`UploadedFileTests.MarkDeleted_CalledTwice_KeepsTheFirstDeletionTime`<br>`UploadedFileTests.MarkAttached_DeletedFile_Throws` | Split into its two rules. |
| 193 | [file] upload attachment -> FileResponse with a signed url | Application.IntegrationTests:<br>`FileTests.UploadFile_MessageAttachment_ReturnsAFileResponseWithASignedUrl` |  |
| 194 | [file] upload avatar -> public /api/files/{id} url | Application.IntegrationTests:<br>`FileTests.UploadFile_Avatar_ReturnsThePublicFileUrl` |  |
| 195 | [file] declared image/png with PDF bytes -> ValidationException (content sniffing) | Application.IntegrationTests:<br>`FileTests.UploadFile_PdfBytesDeclaredAsPng_ThrowsValidationException` |  |
| 196 | [file] avatar upload must be an image | Application.IntegrationTests:<br>`FileTests.UploadFile_NonImageAvatar_ThrowsValidationException` |  |
| 197 | [file] UploadedFile row owned by the caller, bytes under the private storage root | Application.IntegrationTests:<br>`FileTests.UploadFile_Success_StoresARowOwnedByTheCallerAndBytesUnderThePrivateRoot` |  |
| 198 | [file] send image with AttachmentFileId -> response carries a signed url | Application.IntegrationTests:<br>`FileTests.SendMessage_ImageAttachmentFileId_ResponseCarriesASignedUrl` |  |
| 199 | [file] MessageReceived payload carries a signed url too | Application.IntegrationTests:<br>`RealTimeEventTests.SendMessage_Image_BroadcastPayloadCarriesASignedUrl` |  |
| 200 | [file] the file is marked attached | Application.IntegrationTests:<br>`FileTests.SendMessage_ImageAttachmentFileId_MarksTheFileAttached` |  |
| 201 | [file] someone else's FileId -> File.NotOwned | Application.IntegrationTests:<br>`FileTests.SendMessage_SomeoneElsesFile_ReturnsNotOwned` |  |
| 202 | [file] already-attached FileId -> File.AlreadyAttached | Application.IntegrationTests:<br>`FileTests.SendMessage_AlreadyAttachedFile_ReturnsAlreadyAttached` |  |
| 203 | [file] Avatar file as an attachment -> File.WrongPurpose | Application.IntegrationTests:<br>`FileTests.SendMessage_AvatarFileAsAttachment_ReturnsWrongPurpose` |  |
| 204 | [file] unknown FileId -> File.NotFound | Application.IntegrationTests:<br>`FileTests.SendMessage_UnknownFileId_ReturnsFileNotFound` |  |
| 205 | [file] voice message with an audio file | Application.IntegrationTests:<br>`FileTests.SendMessage_VoiceWithAnAudioFile_Succeeds` |  |
| 206 | [file] download: owner | Application.IntegrationTests:<br>`FileTests.GetFileForDownload_Owner_Succeeds` |  |
| 207 | [file] download: member who can see the message | Application.IntegrationTests:<br>`FileTests.GetFileForDownload_MemberWhoCanSeeTheMessage_Succeeds` |  |
| 208 | [file] download: non-member -> NotFound | Application.IntegrationTests:<br>`FileTests.GetFileForDownload_NonMember_ReturnsNotFound` |  |
| 209 | [file] download: valid signature, anonymous | Application.IntegrationTests:<br>`FileTests.GetFileForDownload_AnonymousWithAValidSignature_Succeeds` |  |
| 210 | [file] download: tampered sig / changed exp / no auth -> NotFound | Application.IntegrationTests:<br>`FileTests.GetFileForDownload_AnonymousWithoutAValidSignature_ReturnsNotFound` | Theory, 3 cases |
| 211 | [file] download: expired signature -> NotFound | Application.IntegrationTests:<br>`FileTests.GetFileForDownload_ExpiredSignature_ReturnsNotFound` | `FakeTimeProvider.Advance` replaces the harness's static clock offset. |
| 212 | [file] download: avatars are public | Application.IntegrationTests:<br>`FileTests.GetFileForDownload_AvatarAnonymously_Succeeds` |  |
| 213 | [file] download: message hidden by clear -> NotFound for that member | Application.IntegrationTests:<br>`FileTests.GetFileForDownload_MessageHiddenByClear_ReturnsNotFoundForThatMember` |  |
| 214 | [file] profile avatar -> /api/files/{id} | Application.IntegrationTests:<br>`FileTests.UpdateMyProfile_AvatarFile_SetsTheAvatarUrl` |  |
| 215 | [file] replacing the avatar marks the old file deleted | Application.IntegrationTests:<br>`FileTests.UpdateMyProfile_ReplacingTheAvatar_MarksTheOldFileDeleted` |  |
| 216 | [file] RemoveAvatar clears the avatar and deletes the file | Application.IntegrationTests:<br>`FileTests.UpdateMyProfile_RemoveAvatar_ClearsTheAvatarAndDeletesTheFile` |  |
| 217 | [file] someone else's file as avatar -> 403 | Application.IntegrationTests:<br>`FileTests.UpdateMyProfile_SomeoneElsesFileAsAvatar_ReturnsNotOwned` |  |
| 218 | [file] CreateGroup with ImageFileId | Application.IntegrationTests:<br>`FileTests.CreateGroup_WithImageFileId_SetsTheGroupImageUrl` |  |
| 219 | [file] UpdateGroupInfo replaces the image and deletes the old file | Application.IntegrationTests:<br>`FileTests.UpdateGroupInfo_NewImage_ReplacesTheImageAndDeletesTheOldFile` |  |
| 220 | [file] deleting the message marks its file deleted | Application.IntegrationTests:<br>`FileTests.DeleteMessage_WithAnAttachment_MarksTheFileDeleted` |  |
| 221 | [file] cleanup removes deleted files from disk and DB | Application.IntegrationTests:<br>`FileCleanupTests.RunOnce_DeletedFile_RemovesItFromDiskAndDatabase` |  |
| 222 | [file] cleanup keeps a fresh unattached upload | Application.IntegrationTests:<br>`FileCleanupTests.RunOnce_FreshUnattachedUpload_KeepsIt` |  |
| 223 | [file] unattached upload older than 24h removed from disk and DB | Application.IntegrationTests:<br>`FileCleanupTests.RunOnce_UnattachedUploadOlderThanTheOrphanAge_RemovesItFromDiskAndDatabase` | `FakeTimeProvider.Advance` past `OrphanAge`. |
| 224 | [file] attached files survive cleanup | Application.IntegrationTests:<br>`FileCleanupTests.RunOnce_AttachedFilesOlderThanTheOrphanAge_KeepsThem` |  |
| 225 | [file] ChangePassword and DeleteMyAccount call DisconnectUserAsync | Application.IntegrationTests:<br>`SecurityTests.CredentialChange_Success_DisconnectsTheUsersLiveConnections` | Theory, 2 cases (ChangePassword, DeleteMyAccount) |
| 226 | [file] DisconnectUserAsync aborts every tracked connection of that user, and only theirs | Application.IntegrationTests:<br>`ChatHubTests.DisconnectUser_TrackedConnections_AbortsEveryConnectionOfThatUserOnly` |  |
| 227 | missing user id -> NotAuthenticatedException | Application.IntegrationTests:<br>`UserTests.GetMe_WithoutUserId_ThrowsNotAuthenticatedException` |  |
| 228 | no unexpected handler errors | Application.IntegrationTests:<br>`AppTestBase.DisposeAsync` | Runs after every Application test and fails it if any `ChatHub.*` logger wrote an error that the test didn't expect. |

## apitest.cs (140 checks)

| # | Harness check | Replaced by | Notes |
|---|---|---|---|
| 1 | migrations applied: 20261115121029_InitialCreate, 20261115142718_AddSecurityStamp, 20261115145404_AddUploadedFiles | Application.IntegrationTests:<br>`PersistenceTests.Migrate_FreshDatabase_AppliesAllMigrationsInOrder` | Schema check, so it lives with the other persistence tests. |
| 2 | [sec3] AddSecurityStamp: required uuid column, existing users backfilled with distinct random stamps | Application.IntegrationTests:<br>`PersistenceTests.Migrate_AddSecurityStampOverExistingUsers_BackfillsDistinctRequiredStamps` | Uses its own throwaway database on the shared container. |
| 3 | xmin row version did not create a column | Application.IntegrationTests:<br>`PersistenceTests.Migrate_RefreshTokensTable_HasNoXminUserColumn` |  |
| 4 | partial unique, DirectKey and message keyset indexes exist | Application.IntegrationTests:<br>`PersistenceTests.Migrate_Indexes_CreatesPartialUniqueContactDirectKeyAndMessageKeysetIndexes` |  |
| 5 | API started | Api.IntegrationTests:<br>`ApiFixture.InitializeAsync`<br>`InfrastructureEndpointTests.Health_Get_Returns200Healthy` | There's no separate process to wait for any more: the fixture boots `Program` in-process with `WebApplicationFactory`, and every API test fails if it doesn't start. |
| 6 | GET /health -> 200 Healthy | Api.IntegrationTests:<br>`InfrastructureEndpointTests.Health_Get_Returns200Healthy` | Theory, 3 cases: `/health`, `/health/live`, `/health/ready` |
| 7 | OpenAPI: Bearer security scheme | Api.IntegrationTests:<br>`InfrastructureEndpointTests.OpenApiDocument_Get_DeclaresTheBearerSecurityScheme` |  |
| 8 | OpenAPI: protected operations require Bearer, anonymous ones do not | Api.IntegrationTests:<br>`InfrastructureEndpointTests.OpenApiDocument_Get_RequiresBearerOnProtectedOperationsOnly` |  |
| 9 | Scalar UI served in Development | Api.IntegrationTests:<br>`InfrastructureEndpointTests.ScalarUi_InDevelopment_IsServed` |  |
| 10 | CORS preflight for the React dev origin | Api.IntegrationTests:<br>`InfrastructureEndpointTests.CorsPreflight_FromTheReactDevOrigin_IsAllowedWithCredentials` |  |
| 11 | register -> 201 + Location + token pair | Api.IntegrationTests:<br>`AuthTests.Register_ValidEmailUser_Returns201WithLocationAndTokenPair` |  |
| 12 | register second user | Api.IntegrationTests:<br>`AuthTests.Register_ValidPhoneUser_Returns201` |  |
| 13 | duplicate email -> 409 ProblemDetails with code + traceId | Api.IntegrationTests:<br>`AuthTests.Register_DuplicateEmail_Returns409ProblemWithCodeAndTraceId` |  |
| 14 | validation error -> 400 with errors dictionary | Api.IntegrationTests:<br>`AuthTests.Register_InvalidBody_Returns400ProblemWithErrorsDictionary` |  |
| 15 | login -> 200 | Api.IntegrationTests:<br>`AuthTests.Login_ValidCredentials_Returns200ForThatUser` |  |
| 16 | refresh rotates the pair | Api.IntegrationTests:<br>`AuthTests.Refresh_ValidToken_Returns200WithANewPair` |  |
| 17 | logout -> 204 (anonymous) | Api.IntegrationTests:<br>`AuthTests.Logout_Anonymous_Returns204` |  |
| 18 | refresh after logout -> 400 Auth.InvalidRefreshToken | Api.IntegrationTests:<br>`AuthTests.Refresh_AfterLogout_Returns400InvalidRefreshToken` |  |
| 19 | change-password -> 200 with a new pair | Api.IntegrationTests:<br>`AuthTests.ChangePassword_ValidRequest_Returns200WithANewPair` |  |
| 20 | [sec4] access token carries the user's security stamp (sstamp) | Api.IntegrationTests:<br>`AuthTests.Login_AccessToken_CarriesTheUsersSecurityStamp` |  |
| 21 | [sec5] after ChangePassword the old access token -> 401, the new one -> 200 | Api.IntegrationTests:<br>`AuthTests.ChangePassword_Success_OldAccessTokenIsRejectedAndTheNewOneAccepted` |  |
| 22 | register third user (from another address) | Api.IntegrationTests:<br>`RateLimitingTests.Register_FromAnotherAddressWhileOneIsLimited_Returns201` | The harness check registered from a second client address. The test now first exhausts the other address's budget, so it proves something. |
| 23 | no token -> 401 ProblemDetails | Api.IntegrationTests:<br>`AuthenticationTests.ProtectedEndpoint_WithoutAToken_Returns401ProblemWithBearerChallenge` |  |
| 24 | garbage token -> 401 | Api.IntegrationTests:<br>`AuthenticationTests.ProtectedEndpoint_GarbageToken_Returns401` |  |
| 25 | [fix1] valid JWT without a user id -> 401 ProblemDetails + WWW-Authenticate: Bearer (now rejected at token validation) | Api.IntegrationTests:<br>`AuthenticationTests.ProtectedEndpoint_ValidJwtWithoutAUserId_Returns401ProblemWithBearerChallenge` |  |
| 26 | [sec5] token without a security stamp -> 401 | Api.IntegrationTests:<br>`AuthenticationTests.ProtectedEndpoint_TokenWithoutASecurityStamp_Returns401` | Tokens are signed with the JWT settings read from the running app, not from `appsettings.Development.json` on disk. |
| 27 | [sec5] token with a wrong security stamp -> 401 | Api.IntegrationTests:<br>`AuthenticationTests.ProtectedEndpoint_TokenWithAWrongSecurityStamp_Returns401` |  |
| 28 | [sec5] control: same token with the current stamp -> 200 | Api.IntegrationTests:<br>`AuthenticationTests.ProtectedEndpoint_ForgedTokenWithTheCurrentSecurityStamp_Returns200` |  |
| 29 | [fix2] auth-strict: 10 logins pass, the 11th -> 429 ProblemDetails + Retry-After | Api.IntegrationTests:<br>`RateLimitingTests.Login_MoreThanTenPerMinuteFromOneAddress_Returns429ProblemWithRetryAfter` | Client addresses come from a test-only startup filter (see the notes below). |
| 30 | [fix2] auth-strict is per client address (127.0.0.1 still allowed) | Api.IntegrationTests:<br>`RateLimitingTests.Login_FromAnotherAddressWhileOneIsLimited_IsAllowed` |  |
| 31 | [fix2] 15 refresh calls in a row from the address that is login-limited -> none rejected | Api.IntegrationTests:<br>`RateLimitingTests.Refresh_FromAnAddressLimitedOnLogin_IsNotRejected` |  |
| 32 | [fix2] register and login share the auth-strict budget (5 + 5 pass, then 429) | Api.IntegrationTests:<br>`RateLimitingTests.RegisterAndLogin_FromOneAddress_ShareTheAuthStrictBudget` |  |
| 33 | [fix2] auth-session: 60 refresh calls pass, the 61st -> 429 | Api.IntegrationTests:<br>`RateLimitingTests.Refresh_MoreThanSixtyPerMinuteFromOneAddress_Returns429` |  |
| 34 | GET /api/users/me | Api.IntegrationTests:<br>`UserTests.GetMe_Authenticated_Returns200WithTheProfile` |  |
| 35 | PUT /api/users/me -> 204 | Api.IntegrationTests:<br>`UserTests.UpdateMe_ValidBody_Returns204` |  |
| 36 | PUT /api/users/me with an empty / unknown avatarFileId -> 400 / 404 (was: javascript: avatar -> 400) | Api.IntegrationTests:<br>`UserTests.UpdateMe_EmptyOrUnknownAvatarFileId_Returns400Or404` |  |
| 37 | search users | Api.IntegrationTests:<br>`UserTests.SearchUsers_ByName_Returns200WithTheMatch` |  |
| 38 | search with a 1-char query -> 400 | Api.IntegrationTests:<br>`UserTests.SearchUsers_OneCharacterQuery_Returns400` |  |
| 39 | GET /api/users/{id} | Api.IntegrationTests:<br>`UserTests.GetUserProfile_ExistingUser_Returns200` |  |
| 40 | unknown user -> 404 with code | Api.IntegrationTests:<br>`UserTests.GetUserProfile_UnknownUser_Returns404ProblemWithCode` |  |
| 41 | block -> 204 | Api.IntegrationTests:<br>`UserTests.BlockUser_ExistingUser_Returns204` |  |
| 42 | GET /api/users/me/blocked | Api.IntegrationTests:<br>`UserTests.GetMyBlockedUsers_AfterBlocking_ListsTheBlockedUser` |  |
| 43 | unblock -> 204 | Api.IntegrationTests:<br>`UserTests.UnblockUser_BlockedUser_Returns204` |  |
| 44 | Bob connected to /hubs/chat with ?access_token= | Api.IntegrationTests:<br>`RealTimeTests.Hub_ConnectWithAccessTokenQuery_Connects` |  |
| 45 | hub without a token -> 401 | Api.IntegrationTests:<br>`RealTimeTests.Hub_ConnectWithoutAToken_IsRejectedWith401` |  |
| 46 | create direct -> 201 + Location | Api.IntegrationTests:<br>`ConversationTests.CreateDirect_New_Returns201WithLocation` |  |
| 47 | create direct again (other side) -> 200 same id | Api.IntegrationTests:<br>`ConversationTests.CreateDirect_ExistingFromTheOtherSide_Returns200WithTheSameId` |  |
| 48 | SignalR: Bob receives ConversationCreated (title = Alice's name, enum as string) | Api.IntegrationTests:<br>`RealTimeTests.CreateDirect_New_OtherUserReceivesConversationCreatedWithStringEnums` |  |
| 49 | domain rule (leave a direct chat) -> 422 with the rule message | Api.IntegrationTests:<br>`ConversationTests.LeaveConversation_Direct_Returns422ProblemWithTheRuleMessage` |  |
| 50 | not a member -> 403 with code | Api.IntegrationTests:<br>`ConversationTests.GetConversation_NonMember_Returns403WithCode` |  |
| 51 | unknown conversation -> 404 with code | Api.IntegrationTests:<br>`ConversationTests.GetConversation_UnknownId_Returns404WithCode` |  |
| 52 | non-guid id does not match a route -> 404 | Api.IntegrationTests:<br>`ConversationTests.GetConversation_NonGuidId_DoesNotMatchARouteAndReturns404` |  |
| 53 | send text ("type": "Text") -> 201 + Location | Api.IntegrationTests:<br>`MessageTests.SendMessage_TextTypeAsString_Returns201WithLocation` |  |
| 54 | SignalR: Bob receives MessageReceived | Api.IntegrationTests:<br>`RealTimeTests.SendMessage_Text_OtherMemberReceivesMessageReceived` |  |
| 55 | [sec10] GET /api/messages/{id} (the POST's Location) -> 200 for a member | Api.IntegrationTests:<br>`MessageTests.GetMessage_ThePostLocationAsMember_Returns200` |  |
| 56 | [sec10] GET /api/messages/{id} -> 403 for a non-member | Api.IntegrationTests:<br>`MessageTests.GetMessage_NonMember_Returns403WithCode` |  |
| 57 | [sec10] GET /api/messages/{id} -> 404 for an unknown id | Api.IntegrationTests:<br>`MessageTests.GetMessage_UnknownId_Returns404WithCode` |  |
| 58 | System message from a client -> 400 | Api.IntegrationTests:<br>`MessageTests.SendMessage_SystemTypeFromAClient_Returns400` |  |
| 59 | unknown enum string -> 400 with errors | Api.IntegrationTests:<br>`MessageTests.SendMessage_UnknownTypeString_Returns400ProblemWithErrors` |  |
| 60 | image message without attachmentFileId -> 400 (was: attachment outside /uploads/) | Api.IntegrationTests:<br>`MessageTests.SendMessage_ImageWithoutAttachmentFileId_Returns400` |  |
| 61 | edit -> 200 MessageResponse | Api.IntegrationTests:<br>`MessageTests.EditMessage_BySender_Returns200WithTheEditedMessage` |  |
| 62 | SignalR: Bob receives MessageUpdated | Api.IntegrationTests:<br>`RealTimeTests.EditMessage_BySender_OtherMemberReceivesMessageUpdated` |  |
| 63 | edit someone else's message -> 422 | Api.IntegrationTests:<br>`MessageTests.EditMessage_SomeoneElsesMessage_Returns422` |  |
| 64 | delete -> 204 | Api.IntegrationTests:<br>`MessageTests.DeleteMessage_BySender_Returns204` |  |
| 65 | SignalR: Bob receives MessageDeleted | Api.IntegrationTests:<br>`RealTimeTests.DeleteMessage_BySender_OtherMemberReceivesMessageDeleted` |  |
| 66 | mark read -> 204 | Api.IntegrationTests:<br>`MessageTests.MarkAsRead_OtherMembersMessage_Returns204` |  |
| 67 | SignalR: Bob receives MessagesRead | Api.IntegrationTests:<br>`RealTimeTests.MarkAsRead_OtherMembersMessage_SenderReceivesMessagesRead` |  |
| 68 | mark delivered (already delivered) -> 204 | Api.IntegrationTests:<br>`MessageTests.MarkAsDelivered_MessageAlreadyMarkedRead_Returns204` |  |
| 69 | mark unknown message -> 404 Message.NotFound | Api.IntegrationTests:<br>`MessageTests.MarkAsRead_UnknownMessage_Returns404MessageNotFound` |  |
| 70 | GET read-state | Api.IntegrationTests:<br>`MessageTests.GetReadState_AfterMarkingRead_ShowsTheReadPointer` |  |
| 71 | GET messages newest-first with cursor (m3,m2 \| m1,hello alice) | Api.IntegrationTests:<br>`MessageTests.GetMessages_WithCursor_PagesNewestFirst` |  |
| 72 | invalid cursor -> 400 | Api.IntegrationTests:<br>`MessageTests.GetMessages_InvalidCursor_Returns400` |  |
| 73 | messages of a conversation you are not in -> 403 | Api.IntegrationTests:<br>`MessageTests.GetMessages_NonMember_Returns403` |  |
| 74 | [sec9] mark your own message read/delivered -> 204 | Api.IntegrationTests:<br>`MessageTests.MarkOwnMessage_ReadAndDelivered_Returns204` |  |
| 75 | [sec9] ...Alice's read/delivered pointers are unchanged | Api.IntegrationTests:<br>`MessageTests.MarkOwnMessage_ReadAndDelivered_LeavesTheCallersPointersUnchanged` |  |
| 76 | [sec9] ...Bob's earlier message is still unread | Api.IntegrationTests:<br>`MessageTests.MarkOwnMessage_AsRead_KeepsTheOtherMembersEarlierMessageUnread` |  |
| 77 | [sec9] ...and Bob receives no MessagesRead/MessagesDelivered for it | Api.IntegrationTests:<br>`RealTimeTests.MarkOwnMessage_ReadAndDelivered_OtherMemberReceivesNoReceipt` | The harness slept 1 s and then looked. The test now sends a sentinel message and waits for it: SignalR keeps per-connection order, so a receipt sent earlier would already have arrived. |
| 78 | SignalR: Bob receives PresenceChanged online when Alice connects | Api.IntegrationTests:<br>`RealTimeTests.Hub_UserConnects_PeerReceivesPresenceOnline` |  |
| 79 | SignalR: Bob receives UserTyping (hub method Typing) | Api.IntegrationTests:<br>`RealTimeTests.Hub_TypingInvoked_PeerReceivesUserTyping` |  |
| 80 | SignalR: Bob receives PresenceChanged offline when Alice disconnects | Api.IntegrationTests:<br>`RealTimeTests.Hub_UserDisconnects_PeerReceivesPresenceOfflineWithLastSeen` |  |
| 81 | create group -> 201 | Api.IntegrationTests:<br>`ConversationTests.CreateGroup_ValidRequest_Returns201WithLocationAndBothMembers` |  |
| 82 | SignalR: Bob receives ConversationCreated for the group | Api.IntegrationTests:<br>`RealTimeTests.CreateGroup_WithMember_MemberReceivesConversationCreated` |  |
| 83 | add member -> 204 | Api.IntegrationTests:<br>`ConversationTests.AddMember_ByOwner_Returns204` |  |
| 84 | SignalR: Bob receives MemberAdded | Api.IntegrationTests:<br>`RealTimeTests.AddMember_ToGroup_ExistingMemberReceivesMemberAdded` |  |
| 85 | change role ("role": "Admin") -> 204 | Api.IntegrationTests:<br>`ConversationTests.ChangeMemberRole_RoleAsString_Returns204` |  |
| 86 | details show roles as strings | Api.IntegrationTests:<br>`ConversationTests.GetConversation_Group_ReturnsRolesAsStrings` |  |
| 87 | role Owner via change-role -> 400 | Api.IntegrationTests:<br>`ConversationTests.ChangeMemberRole_ToOwner_Returns400` |  |
| 88 | update group info -> 204 | Api.IntegrationTests:<br>`ConversationTests.UpdateGroupInfo_ByOwner_Returns204` |  |
| 89 | SignalR: Bob receives ConversationUpdated | Api.IntegrationTests:<br>`RealTimeTests.UpdateGroupInfo_ByOwner_MemberReceivesConversationUpdated` |  |
| 90 | kick -> 204 | Api.IntegrationTests:<br>`ConversationTests.KickMember_ByOwner_Returns204` |  |
| 91 | SignalR: Bob receives MemberRemoved | Api.IntegrationTests:<br>`RealTimeTests.KickMember_ByOwner_MemberReceivesMemberRemovedKicked` |  |
| 92 | mute -> 204 | Api.IntegrationTests:<br>`ConversationTests.Mute_Member_Returns204` |  |
| 93 | GET conversations page 1 (newest = group, muted) | Api.IntegrationTests:<br>`ConversationTests.GetConversations_FirstPage_ReturnsTheNewestMutedGroupAndHasMore` |  |
| 94 | GET conversations page 2 via cursor | Api.IntegrationTests:<br>`ConversationTests.GetConversations_SecondPageViaCursor_ReturnsTheOlderConversationAndNoMore` |  |
| 95 | unmute -> 204 | Api.IntegrationTests:<br>`ConversationTests.Unmute_MutedMember_Returns204` |  |
| 96 | clear -> 204 | Api.IntegrationTests:<br>`ConversationTests.Clear_Member_Returns204` |  |
| 97 | cleared chat has no visible messages | Api.IntegrationTests:<br>`ConversationTests.Clear_Member_HidesEveryEarlierMessage` |  |
| 98 | [sec10] GET /api/messages/{id} -> 404 for a message hidden by clear (still 200 for the other member) | Api.IntegrationTests:<br>`MessageTests.GetMessage_HiddenByClear_Returns404OnlyForTheMemberWhoCleared` |  |
| 99 | [sec10] GET /api/messages/{id} returns a deleted message masked | Api.IntegrationTests:<br>`MessageTests.GetMessage_DeletedMessage_ReturnsItMasked` |  |
| 100 | transfer ownership -> 204 | Api.IntegrationTests:<br>`ConversationTests.TransferOwnership_ToMember_Returns204` |  |
| 101 | leave group -> 204 | Api.IntegrationTests:<br>`ConversationTests.LeaveConversation_GroupAfterTransferringOwnership_Returns204` |  |
| 102 | SignalR: Bob receives MemberRemoved (Left) | Api.IntegrationTests:<br>`RealTimeTests.LeaveConversation_Group_MemberReceivesMemberRemovedLeft` |  |
| 103 | [files] POST /api/files?purpose=MessageAttachment -> 201 FileResponse {id, fileName, contentType, sizeBytes, url (signed)} + Location | Api.IntegrationTests:<br>`FileTests.Upload_MessageAttachment_Returns201FileResponseWithSignedUrlAndLocation` |  |
| 104 | [files] bytes live in private storage; /uploads/... -> 404 (static serving removed) | Api.IntegrationTests:<br>`FileTests.Upload_Success_StoresBytesPrivatelyWithoutStaticServing` |  |
| 105 | [fix1] UnauthorizedAccessException from the file system -> 500 ProblemDetails, no 401, no details leaked | Api.IntegrationTests:<br>`FileTests.Upload_StorageFolderNotWritable_Returns500ProblemWithoutLeakingDetails` | Windows only: `[WindowsOnlyFact]` skips it on other systems. |
| 106 | [fix1] uploads work again once the folder is writable | Api.IntegrationTests:<br>`FileTests.Upload_StorageFolderWritableAgain_Returns201` | Windows only. |
| 107 | [files] send image with attachmentFileId -> attachment {fileId, fileName, contentType, sizeBytes, url (signed)} | Api.IntegrationTests:<br>`FileTests.SendMessage_ImageAttachmentFileId_Returns201WithSignedAttachment` |  |
| 108 | [files] SignalR MessageReceived carries a signed url | Api.IntegrationTests:<br>`RealTimeTests.SendMessage_ImageAttachment_OtherMemberReceivesASignedUrl` |  |
| 109 | [files] signed url downloads without a token (inline image, nosniff, Cache-Control: private) | Api.IntegrationTests:<br>`FileTests.Download_SignedUrlWithoutAToken_ReturnsTheInlineImagePrivatelyCached` |  |
| 110 | [files] tampered signature -> 404 | Api.IntegrationTests:<br>`FileTests.Download_TamperedSignature_Returns404` |  |
| 111 | [files] correctly signed but expired url -> 404 | Api.IntegrationTests:<br>`FileTests.Download_CorrectlySignedButExpiredUrl_Returns404` | The expired URL is signed with the app's own `IFileUrlSigner` rather than a key re-implemented in the test. |
| 112 | [files] member of the conversation with a token (no signature) -> 200 | Api.IntegrationTests:<br>`FileTests.Download_ConversationMemberWithATokenAndNoSignature_Returns200` |  |
| 113 | [files] non-member with a token / anonymous without a signature -> 404 | Api.IntegrationTests:<br>`FileTests.Download_NonMemberWithATokenOrAnonymousWithoutASignature_Returns404` |  |
| 114 | [files] someone else's FileId -> 403 File.NotOwned | Api.IntegrationTests:<br>`FileTests.SendMessage_SomeoneElsesFile_Returns403NotOwned` |  |
| 115 | [files] already-attached FileId -> 400 File.AlreadyAttached | Api.IntegrationTests:<br>`FileTests.SendMessage_AlreadyAttachedFile_Returns400AlreadyAttached` |  |
| 116 | [files] Avatar file as an attachment -> 400 File.WrongPurpose | Api.IntegrationTests:<br>`FileTests.SendMessage_AvatarFileAsAttachment_Returns400WrongPurpose` |  |
| 117 | [files] declared image/png but PDF bytes -> 400 (content sniffing) | Api.IntegrationTests:<br>`FileTests.Upload_PdfBytesDeclaredAsPng_Returns400ContentMismatch` |  |
| 118 | [files] non-image avatar -> 400 | Api.IntegrationTests:<br>`FileTests.Upload_NonImageAvatar_Returns400` |  |
| 119 | [files] unknown purpose -> 400 | Api.IntegrationTests:<br>`FileTests.Upload_UnknownPurpose_Returns400` |  |
| 120 | [files] purpose=Avatar -> public url /api/files/{id} | Api.IntegrationTests:<br>`FileTests.Upload_Avatar_Returns201WithThePublicUrl` |  |
| 121 | [files] PUT /api/users/me {avatarFileId} -> profile avatarUrl = /api/files/{id} | Api.IntegrationTests:<br>`FileTests.UpdateMe_AvatarFileId_SetsTheProfileAvatarUrl` |  |
| 122 | [files] avatar downloads anonymously (Cache-Control: public, max-age=86400) | Api.IntegrationTests:<br>`FileTests.Download_AvatarAnonymously_ReturnsItPubliclyCachedForADay` |  |
| 123 | [files] replacing the avatar marks the old file deleted (old url -> 404) | Api.IntegrationTests:<br>`FileTests.UpdateMe_ReplacingTheAvatar_MarksTheOldFileDeletedAndItsUrlReturns404` |  |
| 124 | [files] pdf -> Content-Disposition: attachment; filename=report.pdf + nosniff | Api.IntegrationTests:<br>`FileTests.Download_Pdf_ReturnsAnAttachmentWithTheFileNameAndNoSniff` |  |
| 125 | upload text/html -> 400 | Api.IntegrationTests:<br>`FileTests.Upload_Html_Returns400` |  |
| 126 | upload without a file part -> 400 | Api.IntegrationTests:<br>`FileTests.Upload_WithoutAFilePart_Returns400` |  |
| 127 | upload 20 MB + 1 byte -> 400 | Api.IntegrationTests:<br>`FileTests.Upload_OneByteOverTheMaximumSize_Returns400` |  |
| 128 | upload 25 MB (over the request limit) -> 400 ProblemDetails | Api.IntegrationTests:<br>`FileTests.Upload_OverTheRequestLimit_ReturnsAProblem` | TestServer has no Kestrel body-size limit, so this now exercises the multipart limit that `FileUploadLimitsAttribute` sets (still a 400/413 ProblemDetails). |
| 129 | upload without a token -> 401 | Api.IntegrationTests:<br>`FileTests.Upload_WithoutAToken_Returns401` |  |
| 130 | [files] deleting the message marks its file deleted; its urls -> 404 | Api.IntegrationTests:<br>`FileTests.DeleteMessage_WithAnAttachment_MarksTheFileDeletedAndItsUrlsReturn404` |  |
| 131 | [live] ChangePassword closes the user's open SignalR connection | Api.IntegrationTests:<br>`RealTimeTests.ChangePassword_WithAnOpenHubConnection_ClosesIt` |  |
| 132 | [live] reconnecting with the new access token works | Api.IntegrationTests:<br>`RealTimeTests.ChangePassword_NewAccessToken_CanReconnectToTheHub` |  |
| 133 | [live] DeleteMyAccount closes the user's open SignalR connection | Api.IntegrationTests:<br>`RealTimeTests.DeleteMe_WithAnOpenHubConnection_ClosesIt` |  |
| 134 | before deletion Carol can message Alice and connect to the hub | Api.IntegrationTests:<br>`AccountDeletionTests.BeforeDeletion_User_CanMessageAndConnectToTheHub` |  |
| 135 | DELETE /api/users/me -> 204 | Api.IntegrationTests:<br>`AccountDeletionTests.DeleteMe_Authenticated_Returns204` |  |
| 136 | [sec5] deleted user's still-valid token -> 401 (was 404 User.NotFound before the stamp check) | Api.IntegrationTests:<br>`AccountDeletionTests.DeleteMe_StillValidAccessToken_Returns401Problem` |  |
| 137 | [sec5] deleted user's token -> 401 on any endpoint (send message in a direct chat, GET conversations) | Api.IntegrationTests:<br>`AccountDeletionTests.DeleteMe_StillValidAccessToken_Returns401OnAnyEndpoint` | Theory, 2 cases |
| 138 | [sec5] deleted user's SignalR connection is rejected | Api.IntegrationTests:<br>`AccountDeletionTests.DeleteMe_StillValidAccessToken_HubConnectionIsRejectedWith401` |  |
| 139 | [fix1] the 500 was logged once, with the UnauthorizedAccessException | Api.IntegrationTests:<br>`FileTests.Upload_StorageFolderNotWritable_LogsOneErrorWithTheAccessException` | Windows only. |
| 140 | API logged no other errors | Api.IntegrationTests:<br>`ApiTestBase.DisposeAsync` | Runs after every API test and fails it if the API logged any error the test didn't explicitly take. |
