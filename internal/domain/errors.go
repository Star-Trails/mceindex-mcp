package domain

import (
	"encoding/json"
	"fmt"
)

// MceIndexErrorCode defines domain error codes matching the protocol.
type MceIndexErrorCode string

const (
	ErrCodeBrowserNotFound      MceIndexErrorCode = "BROWSER_NOT_FOUND"
	ErrCodeAccessChallenge      MceIndexErrorCode = "ACCESS_CHALLENGE"
	ErrCodeLoadTimeout          MceIndexErrorCode = "LOAD_TIMEOUT"
	ErrCodePageNotFound         MceIndexErrorCode = "PAGE_NOT_FOUND"
	ErrCodeIndicatorNotFound    MceIndexErrorCode = "INDICATOR_NOT_FOUND"
	ErrCodeIndexEmpty           MceIndexErrorCode = "INDEX_EMPTY"
	ErrCodeInvalidConfiguration MceIndexErrorCode = "INVALID_CONFIGURATION"
	ErrCodeExtractionFailed     MceIndexErrorCode = "EXTRACTION_FAILED"
	ErrCodeDatabaseError        MceIndexErrorCode = "DATABASE_ERROR"
	ErrCodeInternalError        MceIndexErrorCode = "INTERNAL_ERROR"
)

// MceIndexError represents a structured domain error.
type MceIndexError struct {
	Code    MceIndexErrorCode      `json:"code"`
	Message string                 `json:"message"`
	Details map[string]interface{} `json:"details,omitempty"`
	Cause   error                  `json:"-"`
}

func (e *MceIndexError) Error() string {
	if e.Cause != nil {
		return fmt.Sprintf("%s: %s (cause: %v)", e.Code, e.Message, e.Cause)
	}
	return fmt.Sprintf("%s: %s", e.Code, e.Message)
}

func (e *MceIndexError) Unwrap() error {
	return e.Cause
}

// ToProtocolEnvelope marshals the error into standard MCP error format.
func (e *MceIndexError) ToProtocolEnvelope() string {
	envelope := map[string]interface{}{
		"error": map[string]interface{}{
			"code":    string(e.Code),
			"message": e.Message,
			"details": e.Details,
		},
	}
	b, _ := json.Marshal(envelope)
	return string(b)
}

func NewError(code MceIndexErrorCode, message string, details ...map[string]interface{}) *MceIndexError {
	var det map[string]interface{}
	if len(details) > 0 {
		det = details[0]
	}
	return &MceIndexError{
		Code:    code,
		Message: message,
		Details: det,
	}
}

func WrapError(code MceIndexErrorCode, message string, cause error, details ...map[string]interface{}) *MceIndexError {
	var det map[string]interface{}
	if len(details) > 0 {
		det = details[0]
	}
	return &MceIndexError{
		Code:    code,
		Message: message,
		Details: det,
		Cause:   cause,
	}
}

func NewInvalidConfigError(msg string) *MceIndexError {
	return NewError(ErrCodeInvalidConfiguration, msg)
}

func NewPageNotFoundError(slug string, available []string) *MceIndexError {
	return NewError(ErrCodePageNotFound, fmt.Sprintf("Page %s was not found in the local index.", slug), map[string]interface{}{
		"available": available,
	})
}

func NewIndicatorNotFoundError(indicator string, available []string) *MceIndexError {
	return NewError(ErrCodeIndicatorNotFound, fmt.Sprintf("Indicator %s was not found in the local index.", indicator), map[string]interface{}{
		"available": available,
	})
}

func NewIndexEmptyError(msg string) *MceIndexError {
	return NewError(ErrCodeIndexEmpty, msg)
}
