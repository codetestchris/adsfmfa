﻿//******************************************************************************************************************************************************************************************//
// Copyright (c) 2021 abergs (https://github.com/abergs/fido2-net-lib)                                                                                                                      //                        
//                                                                                                                                                                                          //
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"),                                       //
// to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software,   //
// and to permit persons to whom the Software is furnished to do so, subject to the following conditions:                                                                                   //
//                                                                                                                                                                                          //
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.                                                           //
//                                                                                                                                                                                          //
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,                                      //
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,                            //
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.                               //
//                                                                                                                                                                                          //
//******************************************************************************************************************************************************************************************//
// Copyright (c) 2024 redhook (adfsmfa@gmail.com)                                                                                                                                    //                        
//                                                                                                                                                                                          //
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"),                                       //
// to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software,   //
// and to permit persons to whom the Software is furnished to do so, subject to the following conditions:                                                                                   //
//                                                                                                                                                                                          //
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.                                                           //
//                                                                                                                                                                                          //
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,                                      //
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,                            //
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.                               //
//                                                                                                                                                                                          //
//                                                                                                                                                             //
// https://github.com/neos-sdi/adfsmfa                                                                                                                                                      //
//                                                                                                                                                                                          //
//******************************************************************************************************************************************************************************************//
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Neos.IdentityServer.MultiFactor.WebAuthN.Objects;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Neos.IdentityServer.MultiFactor.WebAuthN.Library.Cbor;

namespace Neos.IdentityServer.MultiFactor.WebAuthN.AttestationFormat
{
    internal enum UndesiredAuthenticatorStatus
    {
        ATTESTATION_KEY_COMPROMISE = AuthenticatorStatus.ATTESTATION_KEY_COMPROMISE,
        USER_VERIFICATION_BYPASS = AuthenticatorStatus.USER_VERIFICATION_BYPASS,
        USER_KEY_REMOTE_COMPROMISE = AuthenticatorStatus.USER_KEY_REMOTE_COMPROMISE,
        USER_KEY_PHYSICAL_COMPROMISE = AuthenticatorStatus.USER_KEY_PHYSICAL_COMPROMISE,
        REVOKED = AuthenticatorStatus.REVOKED
    };

    [JsonConverter(typeof(StringEnumConverter))]
    internal enum MetadataAttestationType
    {
        [EnumMember(Value = "basic_full")]
        ATTESTATION_BASIC_FULL = 0x3e07,
        [EnumMember(Value = "basic_surrogate")]
        ATTESTATION_BASIC_SURROGATE = 0x3e08,
        [EnumMember(Value = "ecdaa")]
        ATTESTATION_ECDAA = 0x3e09,
        [EnumMember(Value = "attca")]
        ATTESTATION_PRIVACY_CA = 0x3e10,
        [EnumMember(Value = "anonca")]
        ATTESTATION_ANONCA = 0x3e0c,
        [EnumMember(Value = "none")]
        ATTESTATION_NONE = 0x3e0b
    }

    internal sealed class Packed : AttestationVerifier
    {
        public static bool IsValidPackedAttnCertSubject(string attnCertSubj)
        {
            // parse the DN string using standard rules
            var dictSubjectObj = new X500DistinguishedName(attnCertSubj);

            // form the string for splitting using new lines to avoid issues with commas
            var dictSubjectString = dictSubjectObj.Decode(X500DistinguishedNameFlags.UseNewLines);
            var dictSubject = dictSubjectString.Split(new string[] { Environment.NewLine }, StringSplitOptions.None)
                                          .Select(part => part.Split('='))
                                          .ToDictionary(split => split[0], split => split[1]);

            return (0 != dictSubject["C"].Length &&
                0 != dictSubject["O"].Length &&
                0 != dictSubject["OU"].Length &&
                0 != dictSubject["CN"].Length &&
                "Authenticator Attestation" == dictSubject["OU"].ToString());
        }

        public override (AttestationType, X509Certificate2[]) Verify()
        {
            // 1. Verify that attStmt is valid CBOR conforming to the syntax defined above and 
            // perform CBOR decoding on it to extract the contained fields.
            if (0 == attStmt.Keys.Count || 0 == attStmt.Values.Count)
                throw new VerificationException("Attestation format packed must have attestation statement");

            if (null == Sig || CBORType.ByteString != Sig.Type || 0 == Sig.GetByteString().Length)
                throw new VerificationException("Invalid packed attestation signature");

            if (null == Alg || true != Alg.IsNumber)
                throw new VerificationException("Invalid packed attestation algorithm");

            // 2. If x5c is present, this indicates that the attestation type is not ECDAA
            if (null != X5c)
            {
                if (CBORType.Array != X5c.Type || 0 == X5c.Count || null != EcdaaKeyId)
                    throw new VerificationException("Malformed x5c array in packed attestation statement");
                var enumerator = X5c.Values.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    if (null == enumerator || null == enumerator.Current
                        || CBORType.ByteString != enumerator.Current.Type
                        || 0 == enumerator.Current.GetByteString().Length)
                        throw new VerificationException("Malformed x5c cert found in packed attestation statement");

                    var x5ccert = new X509Certificate2(enumerator.Current.GetByteString());

                    // X509Certificate2.NotBefore/.NotAfter return LOCAL DateTimes, so
                    // it's correct to compare using DateTime.Now.
                    if (DateTime.Now < x5ccert.NotBefore || DateTime.Now > x5ccert.NotAfter)
                        throw new VerificationException("Packed signing certificate expired or not yet valid");
                }

                // The attestation certificate attestnCert MUST be the first element in the array.
                var attestnCert = new X509Certificate2(X5c.Values.First().GetByteString());

                // 2a. Verify that sig is a valid signature over the concatenation of authenticatorData and clientDataHash 
                // using the attestation public key in attestnCert with the algorithm specified in alg
                var cpk = new CredentialPublicKey(attestnCert, Alg.AsInt32());
                if (true != cpk.Verify(Data, Sig.GetByteString()))
                    throw new VerificationException("Invalid full packed signature");

                // Verify that attestnCert meets the requirements in https://www.w3.org/TR/webauthn/#packed-attestation-cert-requirements
                // 2bi. Version MUST be set to 3
                if (3 != attestnCert.Version)
                    throw new VerificationException("Packed x5c attestation certificate not V3");

                // 2bii. Subject field MUST contain C, O, OU, CN
                // OU must match "Authenticator Attestation"
                if (true != IsValidPackedAttnCertSubject(attestnCert.Subject))
                    throw new VerificationException("Invalid attestation cert subject");

                // 2biii. If the related attestation root certificate is used for multiple authenticator models, 
                // the Extension OID 1.3.6.1.4.1.45724.1.1.4 (id-fido-gen-ce-aaguid) MUST be present, containing the AAGUID as a 16-byte OCTET STRING
                // verify that the value of this extension matches the aaguid in authenticatorData
                var aaguid = AaguidFromAttnCertExts(attestnCert.Extensions);

                // 2biiii. The Basic Constraints extension MUST have the CA component set to false
                if (IsAttnCertCACert(attestnCert.Extensions))
                    throw new VerificationException("Attestation certificate has CA cert flag present");

                // 2c. If attestnCert contains an extension with OID 1.3.6.1.4.1.45724.1.1.4 (id-fido-gen-ce-aaguid) verify that the value of this extension matches the aaguid in authenticatorData
                if (aaguid != null)
                {
                    if (0 != AttestedCredentialData.FromBigEndian(aaguid).CompareTo(AuthData.AttestedCredentialData.AaGuid))
                        throw new VerificationException("aaguid present in packed attestation cert exts but does not match aaguid from authData");
                }

                // id-fido-u2f-ce-transports 
                var u2ftransports = U2FTransportsFromAttnCert(attestnCert.Extensions);

                // 2d. Optionally, inspect x5c and consult externally provided knowledge to determine whether attStmt conveys a Basic or AttCA attestation
                var trustPath = X5c.Values
                    .Select(x => new X509Certificate2(x.GetByteString()))
                    .ToArray();

                return (AttestationType.AttCa, trustPath);
            }

            // 3. If ecdaaKeyId is present, then the attestation type is ECDAA
            else if (null != EcdaaKeyId)
            {
                // 3a. Verify that sig is a valid signature over the concatenation of authenticatorData and clientDataHash
                // using ECDAA-Verify with ECDAA-Issuer public key identified by ecdaaKeyId
                // https://www.w3.org/TR/webauthn/#biblio-fidoecdaaalgorithm

                throw new VerificationException("ECDAA is not yet implemented");
                // 3b. If successful, return attestation type ECDAA and attestation trust path ecdaaKeyId.
                // attnType = AttestationType.ECDAA;
                // trustPath = ecdaaKeyId;
            }
            // 4. If neither x5c nor ecdaaKeyId is present, self attestation is in use
            else
            {
                // 4a. Validate that alg matches the algorithm of the credentialPublicKey in authenticatorData
                if (false == AuthData.AttestedCredentialData.CredentialPublicKey.IsSameAlg((COSE.Algorithm)Alg.AsInt32()))
                    throw new VerificationException("Algorithm mismatch between credential public key and authenticator data in self attestation statement");

                // 4b. Verify that sig is a valid signature over the concatenation of authenticatorData and 
                // clientDataHash using the credential public key with alg
                if (true != AuthData.AttestedCredentialData.CredentialPublicKey.Verify(Data, Sig.GetByteString()))
                    throw new VerificationException("Failed to validate signature");

                return (AttestationType.Self, null);
            }
        }
    }
}
