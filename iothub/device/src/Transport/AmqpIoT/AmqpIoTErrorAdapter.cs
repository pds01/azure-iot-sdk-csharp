﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using Microsoft.Azure.Devices.Common;
using Microsoft.Azure.Devices.Client.Exceptions;
using Microsoft.Azure.Amqp;
using Microsoft.Azure.Amqp.Encoding;
using Microsoft.Azure.Amqp.Framing;

namespace Microsoft.Azure.Devices.Client.Transport.AmqpIoT
{
    internal static class AmqpIoTErrorAdapter
    {
        public static readonly AmqpSymbol TimeoutName = AmqpIoTConstants.Vendor + ":timeout";
        public static readonly AmqpSymbol StackTraceName = AmqpIoTConstants.Vendor + ":stack-trace";

        // Error codes
        public static readonly AmqpSymbol DeadLetterName = AmqpIoTConstants.Vendor + ":dead-letter";

        public const string DeadLetterReasonHeader = "DeadLetterReason";
        public const string DeadLetterErrorDescriptionHeader = "DeadLetterErrorDescription";
        public static readonly AmqpSymbol TimeoutError = AmqpIoTConstants.Vendor + ":timeout";
        public static readonly AmqpSymbol MessageLockLostError = AmqpIoTConstants.Vendor + ":message-lock-lost";
        public static readonly AmqpSymbol IotHubNotFoundError = AmqpIoTConstants.Vendor + ":iot-hub-not-found-error";
        public static readonly AmqpSymbol ArgumentError = AmqpIoTConstants.Vendor + ":argument-error";
        public static readonly AmqpSymbol ArgumentOutOfRangeError = AmqpIoTConstants.Vendor + ":argument-out-of-range";
        public static readonly AmqpSymbol DeviceAlreadyExists = AmqpIoTConstants.Vendor + ":device-already-exists";
        public static readonly AmqpSymbol DeviceContainerThrottled = AmqpIoTConstants.Vendor + ":device-container-throttled";
        public static readonly AmqpSymbol PartitionNotFound = AmqpIoTConstants.Vendor + ":partition-not-found";
        public static readonly AmqpSymbol IotHubSuspended = AmqpIoTConstants.Vendor + ":iot-hub-suspended";

        public static readonly AmqpSymbol TrackingId = AmqpIoTConstants.Vendor + ":tracking-id";
        public static readonly AmqpSymbol ClientVersion = AmqpIoTConstants.Vendor + ":client-version";
        public static readonly AmqpSymbol ApiVersion = AmqpIoTConstants.Vendor + ":api-version";
        public static readonly AmqpSymbol ChannelCorrelationId = AmqpIoTConstants.Vendor + ":channel-correlation-id";

        private const int MaxSizeInInfoMap = 32 * 1024;

        public static AmqpException ToAmqpException(Exception exception, string gatewayId)
        {
            return ToAmqpException(exception, gatewayId, false);
        }

        public static AmqpException ToAmqpException(Exception exception, string gatewayId, bool includeStackTrace)
        {
            Error amqpError = ToAmqpError(exception, gatewayId, includeStackTrace);
            return new AmqpException(amqpError);
        }

        public static Error ToAmqpError(Exception exception, string gatewayId)
        {
            return ToAmqpError(exception, gatewayId, false);
        }

        public static Error ToAmqpError(Exception exception, string gatewayId, bool includeStackTrace)
        {
            if (exception == null)
            {
                throw new ArgumentNullException("exception");
            }

            Error error = new Error();
            error.Description = exception.Message;

            if (exception is AmqpException)
            {
                AmqpException amqpException = (AmqpException)exception;
                error.Condition = amqpException.Error.Condition;
                error.Info = amqpException.Error.Info;
            }
            else if (exception is UnauthorizedAccessException || exception is UnauthorizedException)
            {
                error.Condition = AmqpErrorCode.UnauthorizedAccess;
            }
            else if (exception is NotSupportedException)
            {
                error.Condition = AmqpErrorCode.NotImplemented;
            }
            else if (exception is DeviceNotFoundException)
            {
                error.Condition = AmqpErrorCode.NotFound;
            }
            else if (exception is IotHubNotFoundException)
            {
                error.Condition = IotHubNotFoundError;
            }
            else if (exception is DeviceMessageLockLostException)
            {
                error.Condition = MessageLockLostError;
            }
            else if (exception is MessageTooLargeException)
            {
                error.Condition = AmqpErrorCode.MessageSizeExceeded;
            }
            else if (exception is DeviceMaximumQueueDepthExceededException)
            {
                error.Condition = AmqpErrorCode.ResourceLimitExceeded;
            }
            else if (exception is TimeoutException)
            {
                error.Condition = TimeoutError;
            }
            else if (exception is InvalidOperationException)
            {
                error.Condition = AmqpErrorCode.NotAllowed;
            }
            else if (exception is ArgumentOutOfRangeException)
            {
                error.Condition = ArgumentOutOfRangeError;
            }
            else if (exception is ArgumentException)
            {
                error.Condition = ArgumentError;
            }
            else if (exception is IotHubSuspendedException)
            {
                error.Condition = IotHubSuspended;
            }
            else
            {
                error.Condition = AmqpErrorCode.InternalError;
                error.Description = error.Description;
            }
            // we will always need this to add trackingId
            if (error.Info == null)
            {
                error.Info = new Fields();
            }

            string stackTrace;
            if (includeStackTrace && !string.IsNullOrEmpty(stackTrace = exception.StackTrace))
            {
                if (stackTrace.Length > MaxSizeInInfoMap)
                {
                    stackTrace = stackTrace.Substring(0, MaxSizeInInfoMap);
                }

                // error.Info came from AmqpExcpetion then it contains StackTraceName already.
                string dummy;
                if (!error.Info.TryGetValue(StackTraceName, out dummy))
                {
                    error.Info.Add(StackTraceName, stackTrace);
                }
            }

            string trackingId;
            error.Info.TryGetValue(TrackingId, out trackingId);
            trackingId = AmqpIoTTrackingHelper.CheckAndAddGatewayIdToTrackingId(gatewayId, trackingId);
            error.Info.Add(TrackingId, trackingId);

            return error;
        }

        public static Exception GetExceptionFromOutcome(Outcome outcome)
        {
            Exception retException = null;
            if (outcome == null)
            {
                retException = new IotHubException("Unknown error.");
                return retException;
            }

            if (outcome.DescriptorCode == Rejected.Code)
            {
                var rejected = (Rejected)outcome;
                retException = AmqpIoTErrorAdapter.ToIotHubClientContract(rejected.Error);
            }
            else if (outcome.DescriptorCode == Released.Code)
            {
                retException = new OperationCanceledException("AMQP link released.");
            }
            else
            {
                retException = new IotHubException("Unknown error.");
            }

            return retException;
        }

        public static Exception ToIotHubClientContract(AmqpException amqpException)
        {
            Error error = amqpException.Error;
            AmqpSymbol amqpSymbol = error.Condition;
            string message = error.ToString();

            // Generic AMQP error
            if (Equals(AmqpErrorCode.InternalError, amqpSymbol))
            {
                return new IotHubCommunicationException(message, amqpException);
            }
            else if (Equals(AmqpErrorCode.NotFound, amqpSymbol))
            {
                return new DeviceNotFoundException(message, amqpException);
            }
            else if (Equals(AmqpErrorCode.UnauthorizedAccess, amqpSymbol))
            {
                return new UnauthorizedException(message, amqpException);
            }
            else if (Equals(AmqpErrorCode.DecodeError, amqpSymbol))
            {
                return new AmqpIoTResourceException(message, amqpException);
            }
            else if (Equals(AmqpErrorCode.ResourceLimitExceeded, amqpSymbol))
            {
                return new IotHubException(message, amqpException);
            }
            else if (Equals(AmqpErrorCode.NotAllowed, amqpSymbol))
            {
                return new InvalidOperationException(message, amqpException);
            }
            else if (Equals(AmqpErrorCode.InvalidField, amqpSymbol))
            {
                return new InvalidOperationException(message, amqpException);
            }
            else if (Equals(AmqpErrorCode.NotImplemented, amqpSymbol))
            {
                return new NotSupportedException(message, amqpException);
            }
            else if (Equals(AmqpErrorCode.ResourceLocked, amqpSymbol))
            {
                return new AmqpIoTResourceException(message, amqpException, true);
            }
            else if (Equals(AmqpErrorCode.PreconditionFailed, amqpSymbol))
            {
                return new IotHubException(message, amqpException);
            }
            else if (Equals(AmqpErrorCode.ResourceDeleted, amqpSymbol))
            {
                return new IotHubException(message, amqpException);
            }
            else if (Equals(AmqpErrorCode.IllegalState, amqpSymbol))
            {
                return new IotHubException(message, amqpException);
            }
            else if (Equals(AmqpErrorCode.FrameSizeTooSmall, amqpSymbol))
            {
                return new AmqpIoTResourceException(message, amqpException);
            }
            // AMQP Connection Error
            else if (Equals(AmqpErrorCode.ConnectionForced, amqpSymbol))
            {
                return new AmqpIoTResourceException(message, amqpException, true);
            }
            else if (Equals(AmqpErrorCode.FramingError, amqpSymbol))
            {
                return new AmqpIoTResourceException(message, amqpException, true);
            }
            else if (Equals(AmqpErrorCode.ConnectionRedirect, amqpSymbol))
            {
                return new AmqpIoTResourceException(message, amqpException, true);
            }
            // AMQP Session Error
            else if (Equals(AmqpErrorCode.WindowViolation, amqpSymbol))
            {
                return new AmqpIoTResourceException(message, amqpException, true);
            }
            else if (Equals(AmqpErrorCode.ErrantLink, amqpSymbol))
            {
                return new AmqpIoTResourceException(message, amqpException, true);
            }
            else if (Equals(AmqpErrorCode.HandleInUse, amqpSymbol))
            {
                return new AmqpIoTResourceException(message, amqpException, true);
            }
            else if (Equals(AmqpErrorCode.UnattachedHandle, amqpSymbol))
            {
                return new AmqpIoTResourceException(message, amqpException, true);
            }
            // AMQP Link Error
            else if (Equals(AmqpErrorCode.DetachForced, amqpSymbol))
            {
                return new AmqpIoTResourceException(message, amqpException, true);
            }
            else if (Equals(AmqpErrorCode.TransferLimitExceeded, amqpSymbol))
            {
                return new AmqpIoTResourceException(message, amqpException, true);
            }
            else if (Equals(AmqpErrorCode.MessageSizeExceeded, amqpSymbol))
            {
                return new MessageTooLargeException(message, amqpException);
            }
            else if (Equals(AmqpErrorCode.LinkRedirect, amqpSymbol))
            {
                return new AmqpIoTResourceException(message, amqpException, true);
            }
            else if (Equals(AmqpErrorCode.Stolen, amqpSymbol))
            {
                return new AmqpIoTResourceException(message, amqpException, true);
            }
            // AMQP Transaction Error
            else if (Equals(AmqpErrorCode.TransactionUnknownId, amqpSymbol))
            {
                return new IotHubException(message, amqpException);
            }
            else if (Equals(AmqpErrorCode.TransactionRollback, amqpSymbol))
            {
                return new IotHubCommunicationException(message, amqpException);
            }
            else if (Equals(AmqpErrorCode.TransactionTimeout, amqpSymbol))
            {
                return new IotHubCommunicationException(message, amqpException);
            }
            else
            {
                return new IotHubException(message, amqpException);
            }
        }

        public static Exception ToIotHubClientContract(Error error)
        {
            Exception retException;
            if (error == null)
            {
                retException = new IotHubException("Unknown error.");
                return retException;
            }

            string message = error.Description;

            string trackingId = null;
            if (error.Info != null && error.Info.TryGetValue(TrackingId, out trackingId))
            {
                message = "{0}{1}{2}".FormatInvariant(message, Environment.NewLine, "Tracking Id:" + trackingId);
            }

            if (error.Condition.Equals(TimeoutError))
            {
                retException = new TimeoutException(message);
            }
            else if (error.Condition.Equals(AmqpErrorCode.NotFound))
            {
                retException = new DeviceNotFoundException(message, (Exception)null);
            }
            else if (error.Condition.Equals(AmqpErrorCode.NotImplemented))
            {
                retException = new NotSupportedException(message);
            }
            else if (error.Condition.Equals(MessageLockLostError))
            {
                retException = new DeviceMessageLockLostException(message);
            }
            else if (error.Condition.Equals(AmqpErrorCode.NotAllowed))
            {
                retException = new InvalidOperationException(message);
            }
            else if (error.Condition.Equals(AmqpErrorCode.UnauthorizedAccess))
            {
                retException = new UnauthorizedException(message);
            }
            else if (error.Condition.Equals(ArgumentError))
            {
                retException = new ArgumentException(message);
            }
            else if (error.Condition.Equals(ArgumentOutOfRangeError))
            {
                retException = new ArgumentOutOfRangeException(message);
            }
            else if (error.Condition.Equals(AmqpErrorCode.MessageSizeExceeded))
            {
                retException = new MessageTooLargeException(message);
            }
            else if (error.Condition.Equals(AmqpErrorCode.ResourceLimitExceeded))
            {
                retException = new DeviceMaximumQueueDepthExceededException(message);
            }
            else if (error.Condition.Equals(DeviceAlreadyExists))
            {
                retException = new DeviceAlreadyExistsException(message, null);
            }
            else if (error.Condition.Equals(DeviceContainerThrottled))
            {
                retException = new IotHubThrottledException(message, null);
            }
            else if (error.Condition.Equals(IotHubSuspended))
            {
                retException = new IotHubSuspendedException(message);
            }
            else
            {
                retException = new IotHubException(message);
            }

            if (trackingId != null && retException is IotHubException)
            {
                IotHubException iotHubException = (IotHubException)retException;
                iotHubException.TrackingId = trackingId;
            }
            return retException;
        }
    }
}
